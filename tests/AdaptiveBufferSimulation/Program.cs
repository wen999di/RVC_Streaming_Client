using ClientAvalonia.Models;

static class AdaptiveBufferSimulation
{
    private const long MsToNs = 1_000_000L;

    public static void Main()
    {
        StablePacedStreamUsesDeviceFloor();
        TenMillisecondPacketsStillRespectDeviceFloor();
        UserNetworkProtectionFloorIsOptionalAndAdditive();
        ClockDriftDoesNotBecomeUnboundedJitter();
        FrequentLateTailRaisesTheTarget();
        RecoveredNetworkReturnsToDeviceFloor();
        ActualUnderrunFeedsBackIntoTheTarget();
        FolderUploadProgressAggregatesChildren();
        ZeroByteFolderFilesCanComplete();
        ZeroByteServerFilesKeepTheirMetadata();
        FolderExpansionStateDrivesChevronRotation();
        SpeakerRenamePropagatesToEveryAudioFile();
        TrainingModelNamesSkipExistingNumbers();
        Console.WriteLine("Adaptive buffer simulations passed.");
    }

    private static void StablePacedStreamUsesDeviceFloor()
    {
        var estimator = new JitterEstimator { DeviceBufferMs = 30.0 };
        Feed(estimator, 200, _ => 20.0);
        int target = estimator.GetTargetBufferMs(20.0, 5);
        Require(target == 30, $"stable target should reach the 30ms device floor, got {target}ms");
        Require(estimator.BaseTargetMs == 30.0, $"base target should be 30ms, got {estimator.BaseTargetMs}ms");
        Require(estimator.ProtectionMs == 0.0, $"stable network protection should be zero, got {estimator.ProtectionMs}ms");
    }

    private static void TenMillisecondPacketsStillRespectDeviceFloor()
    {
        var estimator = new JitterEstimator { DeviceBufferMs = 30.0 };
        Feed(estimator, 200, _ => 10.0, packetDurationMs: 10.0);
        int target = estimator.GetTargetBufferMs(10.0, 5);
        Require(target == 30, $"10ms packets should still use the 30ms device floor, got {target}ms");
    }

    private static void UserNetworkProtectionFloorIsOptionalAndAdditive()
    {
        var estimator = new JitterEstimator
        {
            DeviceBufferMs = 30.0,
            MinNetworkProtectionMs = 15.0,
        };
        Feed(estimator, 200, _ => 20.0);
        int target = estimator.GetTargetBufferMs(20.0, 5);
        Require(target == 45, $"15ms user protection should raise the 30ms base to 45ms, got {target}ms");
    }

    private static void ClockDriftDoesNotBecomeUnboundedJitter()
    {
        var estimator = new JitterEstimator();
        // 100 ppm device-clock skew: each 20ms arrival is only 0.002ms late.
        Feed(estimator, 1000, _ => 20.002);
        int target = estimator.GetTargetBufferMs(20.0, 5);
        Require(target == 30, $"clock drift inflated target to {target}ms");
        Require(estimator.RfcJitterMs < 0.01, "RFC jitter should remain near the per-packet skew");
    }

    private static void FrequentLateTailRaisesTheTarget()
    {
        var estimator = new JitterEstimator();
        Feed(estimator, 200, packet => packet > 0 && packet % 10 == 0 ? 60.0 : 20.0);
        int target = estimator.GetTargetBufferMs(20.0, 5);
        Require(estimator.LateQuantileMs >= 39.9, "95th percentile did not capture frequent 40ms lateness");
        Require(target >= 70 && target <= 75, $"late-tail target should be about 70ms, got {target}ms");
    }

    private static void RecoveredNetworkReturnsToDeviceFloor()
    {
        var estimator = new JitterEstimator { DeviceBufferMs = 30.0 };
        long mediaNs = 1_000 * MsToNs;
        long arrivalNs = 2_000 * MsToNs;
        int raisedTarget = 0;

        for (int packet = 0; packet < 3_100; packet++)
        {
            estimator.Update(mediaNs, arrivalNs, 20.0);
            if (packet == 100)
            {
                raisedTarget = estimator.GetTargetBufferMs(20.0, 5);
            }

            double arrivalDeltaMs = packet < 100 && packet % 5 == 0 ? 60.0 : 20.0;
            mediaNs += 20 * MsToNs;
            arrivalNs += (long)Math.Round(arrivalDeltaMs * MsToNs);
        }

        int recoveredTarget = estimator.GetTargetBufferMs(20.0, 5);
        Require(raisedTarget >= 70, $"late tail did not raise target before recovery: {raisedTarget}ms");
        Require(recoveredTarget == 30, $"stable network did not return to device floor: {recoveredTarget}ms");
    }

    private static void ActualUnderrunFeedsBackIntoTheTarget()
    {
        var estimator = new JitterEstimator();
        Feed(estimator, 100, _ => 20.0);
        int before = estimator.GetTargetBufferMs(20.0, 5);
        estimator.ReportUnderrun(30.0, 20.0);
        int after = estimator.GetTargetBufferMs(20.0, 5);
        Require(after >= before + 30, $"underrun feedback did not raise target: {before} -> {after}");
    }

    private static void FolderUploadProgressAggregatesChildren()
    {
        var folder = new ServerFileItem { IsUploadFolder = true, IsUploading = true, Status = "正在上传" };
        var first = new ServerFileItem { Name = "a.wav", TotalBytes = 100, UploadParent = folder };
        var second = new ServerFileItem { Name = "b.wav", TotalBytes = 300, UploadParent = folder };
        folder.UploadChildren.Add(first);
        folder.UploadChildren.Add(second);
        first.SentBytes = 50;
        second.SentBytes = 150;
        folder.RefreshFolderProgress();
        Require(folder.TotalBytes == 400 && folder.SentBytes == 200, "folder byte totals were not aggregated");
        Require(Math.Abs(folder.Progress - 0.5) < 0.001, $"folder progress should be 50%, got {folder.Progress}");
        first.UploadCompleted = true;
        Require(folder.DetailText.Contains("1/2 个文件"), "folder completed-file count was not updated");
        folder.IsExpanded = true;
        Require(folder.ShowUploadChildren && folder.ExpandGlyph == "▾", "folder did not expose expanded children");
    }

    private static void ZeroByteFolderFilesCanComplete()
    {
        var folder = new ServerFileItem { IsUploadFolder = true, IsUploading = true, Status = "正在上传" };
        var empty = new ServerFileItem { Name = "empty.wav", TotalBytes = 0, UploadParent = folder };
        folder.UploadChildren.Add(empty);
        empty.UploadCompleted = true;
        folder.RefreshFolderProgress();
        Require(folder.Progress == 1.0, "a completed zero-byte file should finish folder progress");
        Require(folder.DetailText.Contains("1/1 个文件"), "zero-byte completion count was not displayed");
    }

    private static void ZeroByteServerFilesKeepTheirMetadata()
    {
        var modifiedAt = new DateTimeOffset(2026, 7, 27, 15, 15, 19, TimeSpan.Zero);
        var empty = new ServerFileItem
        {
            Name = "dataset/1/1.wav",
            Size = 0,
            ModifiedAt = modifiedAt,
        };

        Require(empty.DetailText == "0 B  2026-07-27 15:15:19",
            $"zero-byte server file metadata was hidden: {empty.DetailText}");
    }

    private static void FolderExpansionStateDrivesChevronRotation()
    {
        var folder = new ServerFileItem { IsFolder = true, ChildCount = 3 };
        var changedProperties = new List<string>();
        folder.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName ?? string.Empty);
        Require(folder.CanExpand && folder.ExpandRotation == 0.0, "collapsed folder chevron state is invalid");
        folder.IsExpanded = true;
        Require(folder.ExpandRotation == 90.0, "expanded folder chevron did not rotate by 90 degrees");
        Require(changedProperties.Contains(nameof(ServerFileItem.ExpandRotation)),
            "folder expansion did not notify the chevron rotation binding");
        changedProperties.Clear();
        folder.IsExpanded = false;
        Require(folder.ExpandRotation == 0.0, "collapsed folder chevron did not return to zero degrees");
        Require(changedProperties.Contains(nameof(ServerFileItem.ExpandRotation)),
            "folder collapse did not notify the chevron rotation binding");
        Require(folder.DetailText == "3 个文件", "folder file count was not displayed");
        Require(folder.ShowRegularFolderIcon, "ordinary folder icon should be visible");
        folder.IsModelRootFolder = true;
        Require(!folder.ShowRegularFolderIcon, "model root should not reuse the ordinary folder icon");
    }

    private static void SpeakerRenamePropagatesToEveryAudioFile()
    {
        var group = new TrainingSpeakerGroup("说话人一");
        var first = new TrainingAudioItem { Name = "dataset/说话人一/a.wav", Speaker = group.Name };
        var second = new TrainingAudioItem { Name = "dataset/说话人一/b.wav", Speaker = group.Name };
        group.Files.Add(first);
        group.Files.Add(second);

        group.Name = "说话人二";

        Require(first.Speaker == "说话人二" && second.Speaker == "说话人二",
            "renaming a speaker card did not update every audio file");
        Require(group.FileCountText == "2 个音频", "speaker card did not report its audio count");
    }

    private static void TrainingModelNamesSkipExistingNumbers()
    {
        Require(TrainingNameHelper.GetAvailableModelName([]) == "my model",
            "the first training model name should be my model");
        Require(TrainingNameHelper.GetAvailableModelName(["my model"]) == "my model 1",
            "the second training model name should be my model 1");
        Require(TrainingNameHelper.GetAvailableModelName(["my model", "my model 1", "my model 2"]) == "my model 3",
            "training model numbering did not skip occupied names");
    }

    private static void Feed(
        JitterEstimator estimator,
        int packets,
        Func<int, double> arrivalDeltaMs,
        double packetDurationMs = 20.0)
    {
        long mediaNs = 1_000 * MsToNs;
        long arrivalNs = 2_000 * MsToNs;
        for (int packet = 0; packet < packets; packet++)
        {
            estimator.Update(mediaNs, arrivalNs, packetDurationMs);
            mediaNs += (long)Math.Round(packetDurationMs * MsToNs);
            arrivalNs += (long)Math.Round(arrivalDeltaMs(packet) * MsToNs);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
