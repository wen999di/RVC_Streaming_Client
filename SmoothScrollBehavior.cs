using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace ClientAvalonia;

/// <summary>
/// Smooths discrete mouse-wheel input without replacing Avalonia's ScrollViewer theme.
/// Precision touchpads and nested scrollable controls retain their native behavior.
/// </summary>
public sealed class SmoothScrollBehavior
{
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<SmoothScrollBehavior, ScrollViewer, bool>(
            "IsEnabled", defaultValue: false);

    static SmoothScrollBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>(OnIsEnabledChanged);
    }

    private SmoothScrollBehavior()
    {
    }

    public static bool GetIsEnabled(ScrollViewer viewer) => viewer.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer viewer, bool value) =>
        viewer.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(ScrollViewer viewer, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            States.GetValue(viewer, static owner => new ScrollState(owner)).Attach();
        }
        else if (States.TryGetValue(viewer, out var state))
        {
            state.Detach();
            States.Remove(viewer);
        }
    }

    private sealed class ScrollState
    {
        private const double SettleThreshold = 0.2;
        private const double PrecisionDeltaThreshold = 0.5;
        private const double WheelStepDip = 72.0;
        private const double SettleVelocityDipPerSecond = 2.0;
        private const double MaximumSpeedDipPerSecond = 1600.0;
        private const double SpringNaturalFrequency = 22.0;
        private const double SpringDampingRatio = 0.90;
        private const double MaximumQueuedNotches = 3.0;
        private const double MaximumFrameSeconds = 1.0 / 60.0;

        private readonly ScrollViewer _viewer;
        private double _visualY;
        private double _targetY;
        private double _lastAppliedY = double.NaN;
        private double _velocityY;
        private long _animationStartTimestamp;
        private TimeSpan? _lastAnimationFrameTime;
        private bool _attached;
        private bool _animating;
        private bool _applyingOffset;
        private bool _animationFramePending;

        public ScrollState(ScrollViewer viewer)
        {
            _viewer = viewer;
        }

        public void Attach()
        {
            if (_attached)
            {
                return;
            }

            _attached = true;
            _viewer.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
            _viewer.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            _viewer.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            _viewer.ScrollChanged += OnScrollChanged;
            _viewer.DetachedFromVisualTree += OnDetached;
        }

        public void Detach()
        {
            if (!_attached)
            {
                return;
            }

            StopAtCurrentVisualPosition();
            _viewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
            _viewer.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            _viewer.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            _viewer.ScrollChanged -= OnScrollChanged;
            _viewer.DetachedFromVisualTree -= OnDetached;
            _attached = false;
        }

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            if (e.Handled || !_viewer.IsEffectivelyEnabled)
            {
                return;
            }

            var deltaY = e.Delta.Y;
            var horizontal = Math.Abs(e.Delta.X) > Math.Abs(deltaY)
                || (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (deltaY == 0 || horizontal)
            {
                StopAtCurrentVisualPosition();
                return;
            }

            // Small deltas are already continuous touchpad/high-resolution-wheel input.
            if (Math.Abs(deltaY) < PrecisionDeltaThreshold)
            {
                StopAtCurrentVisualPosition();
                return;
            }

            if (_viewer.Presenter is ScrollContentPresenter ownPresenter
                && HasNestedScrollOwner(e.Source as Visual, ownPresenter, deltaY))
            {
                StopAtCurrentVisualPosition();
                return;
            }

            var currentY = _animating ? _visualY : _viewer.Offset.Y;
            var maxY = MaximumY;
            if (maxY <= 0)
            {
                e.Handled = !_viewer.IsScrollChainingEnabled;
                return;
            }

            if (!_animating)
            {
                _targetY = currentY;
            }

            var pixelDelta = -deltaY * WheelStepDip;
            var remaining = _targetY - currentY;
            if (_animating
                && Math.Abs(remaining) > SettleThreshold
                && Math.Sign(pixelDelta) != Math.Sign(remaining))
            {
                _targetY = currentY;
            }

            // Bound queued travel so fast wheels stay responsive instead of building a long tail.
            var maximumLead = WheelStepDip * MaximumQueuedNotches;
            var minimumQueuedTarget = Math.Max(0, currentY - maximumLead);
            var maximumQueuedTarget = Math.Min(maxY, currentY + maximumLead);
            var nextTarget = Math.Clamp(
                _targetY + pixelDelta,
                minimumQueuedTarget,
                maximumQueuedTarget);
            if (Math.Abs(nextTarget - currentY) <= SettleThreshold)
            {
                e.Handled = !_viewer.IsScrollChainingEnabled;
                return;
            }

            _targetY = nextTarget;
            Start();
            e.Handled = true;
        }

        private bool HasNestedScrollOwner(
            Visual? source,
            ScrollContentPresenter ownPresenter,
            double deltaY)
        {
            for (var current = source;
                 current is not null && current != ownPresenter && current != _viewer;
                 current = current.GetVisualParent())
            {
                if (current is not ScrollContentPresenter nested)
                {
                    continue;
                }

                var nestedMaximum = Math.Max(0, nested.Extent.Height - nested.Viewport.Height);
                var canScroll = deltaY > 0
                    ? nested.Offset.Y > SettleThreshold
                    : nested.Offset.Y < nestedMaximum - SettleThreshold;
                if (canScroll || !nested.IsScrollChainingEnabled)
                {
                    return true;
                }
            }

            return false;
        }

        private void Start()
        {
            if (_animating)
            {
                RequestNextAnimationFrame();
                return;
            }

            _animating = true;
            _visualY = _viewer.Offset.Y;
            _animationStartTimestamp = Stopwatch.GetTimestamp();
            _lastAnimationFrameTime = null;
            RequestNextAnimationFrame();
        }

        private void RequestNextAnimationFrame()
        {
            if (_animationFramePending)
            {
                return;
            }

            var topLevel = TopLevel.GetTopLevel(_viewer);
            if (topLevel is null)
            {
                StopAtCurrentVisualPosition();
                return;
            }

            _animationFramePending = true;
            topLevel.RequestAnimationFrame(OnAnimationFrame);
        }

        private void OnAnimationFrame(TimeSpan frameTime)
        {
            _animationFramePending = false;
            if (!_animating)
            {
                return;
            }

            double elapsedSeconds;
            if (_lastAnimationFrameTime is { } lastFrameTime)
            {
                elapsedSeconds = (frameTime - lastFrameTime).TotalSeconds;
            }
            else
            {
                elapsedSeconds = Stopwatch.GetElapsedTime(_animationStartTimestamp).TotalSeconds;
            }

            _lastAnimationFrameTime = frameTime;
            elapsedSeconds = Math.Clamp(elapsedSeconds, 0, MaximumFrameSeconds);
            if (elapsedSeconds <= 0)
            {
                RequestNextAnimationFrame();
                return;
            }

            _targetY = Math.Clamp(_targetY, 0, MaximumY);
            var currentY = _visualY;
            var distance = _targetY - currentY;
            if (Math.Abs(distance) <= SettleThreshold
                && Math.Abs(_velocityY) <= SettleVelocityDipPerSecond)
            {
                ApplyOffset(_targetY);
                StopCore();
                return;
            }

            // Solve x'' + 2*zeta*omega*x' + omega^2*(x-target) = 0 exactly for
            // this frame. A new wheel notch moves only the equilibrium point, so velocity
            // remains continuous and viscous damping produces a real, perceptible tail.
            var dampingRate = SpringDampingRatio * SpringNaturalFrequency;
            var dampedFrequency = SpringNaturalFrequency
                * Math.Sqrt(1.0 - SpringDampingRatio * SpringDampingRatio);
            var positionError = currentY - _targetY;
            var velocityCoefficient = (_velocityY + dampingRate * positionError)
                / dampedFrequency;
            var phase = dampedFrequency * elapsedSeconds;
            var cosine = Math.Cos(phase);
            var sine = Math.Sin(phase);
            var decay = Math.Exp(-dampingRate * elapsedSeconds);
            var positionTerm = positionError * cosine + velocityCoefficient * sine;
            var nextPositionError = decay * positionTerm;
            var nextVelocity = decay
                * ((-positionError * dampedFrequency * sine
                    + velocityCoefficient * dampedFrequency * cosine)
                   - dampingRate * positionTerm);

            _velocityY = Math.Clamp(
                nextVelocity,
                -MaximumSpeedDipPerSecond,
                MaximumSpeedDipPerSecond);
            var nextY = _targetY + nextPositionError;
            var maximumY = MaximumY;
            if (nextY <= 0)
            {
                nextY = 0;
                if (_velocityY < 0)
                {
                    _velocityY = 0;
                }
            }
            else if (nextY >= maximumY)
            {
                nextY = maximumY;
                if (_velocityY > 0)
                {
                    _velocityY = 0;
                }
            }

            ApplyOffset(nextY);
            RequestNextAnimationFrame();
        }

        private void StopAtCurrentVisualPosition()
        {
            if (!_animating)
            {
                return;
            }

            StopCore();
        }

        private void ApplyOffset(double y)
        {
            _visualY = y;
            _applyingOffset = true;
            _lastAppliedY = y;
            try
            {
                _viewer.Offset = new Vector(_viewer.Offset.X, y);
            }
            finally
            {
                _applyingOffset = false;
            }
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (!_animating || _applyingOffset || Math.Abs(e.OffsetDelta.Y) <= 0.001)
            {
                return;
            }

            if (double.IsNaN(_lastAppliedY)
                || Math.Abs(_viewer.Offset.Y - _lastAppliedY) > 0.05)
            {
                StopCore();
            }
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => StopAtCurrentVisualPosition();
        private void OnKeyDown(object? sender, KeyEventArgs e) => StopAtCurrentVisualPosition();
        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => StopCore();

        private double MaximumY => Math.Max(0, _viewer.Extent.Height - _viewer.Viewport.Height);

        private void StopCore()
        {
            _animating = false;
            _targetY = _viewer.Offset.Y;
            _visualY = _viewer.Offset.Y;
            _velocityY = 0;
            _lastAppliedY = double.NaN;
            _lastAnimationFrameTime = null;
        }
    }
}
