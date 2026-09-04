using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ClientAvalonia;

/// <summary>
/// A ComboBox whose popup stays alive long enough to animate both opening and closing.
/// </summary>
public sealed class AnimatedComboBox : ComboBox
{
    private static readonly TimeSpan CloseAnimationDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan HoverAnimationDuration = TimeSpan.FromMilliseconds(190);

    private Popup? _popup;
    private TopLevel? _topLevel;
    private Window? _window;
    private bool _isAnimatingClose;
    private int _animationVersion;

    // Reuse the built-in ComboBox theme instead of requiring a duplicate control theme.
    protected override Type StyleKeyOverride => typeof(ComboBox);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_popup is not null)
        {
            _popup.Opened -= Popup_OnOpened;
            _popup.Closed -= Popup_OnClosed;
        }

        base.OnApplyTemplate(e);

        _popup = e.NameScope.Get<Popup>("PART_Popup");
        ConfigureTemplateHoverTransitions();
        Dispatcher.UIThread.Post(ConfigureTemplateHoverTransitions, DispatcherPriority.Loaded);

        // ComboBox normally lets Popup close itself immediately on an outside click.
        // Handling light dismiss here lets the popup remain rendered for its exit animation.
        _popup.IsLightDismissEnabled = false;
        _popup.Opened += Popup_OnOpened;
        _popup.Closed += Popup_OnClosed;
    }

    private void ConfigureTemplateHoverTransitions()
    {
        // FluentTheme draws the visible outline on an inner Border rather than on
        // ComboBox itself. Put the transitions on those rendered borders so hover
        // and focus brush changes interpolate instead of snapping between colors.
        foreach (var border in this.GetVisualDescendants().OfType<Border>())
        {
            border.Transitions = new Transitions
            {
                new BrushTransition
                {
                    Property = Border.BackgroundProperty,
                    Duration = HoverAnimationDuration,
                    Easing = new CubicEaseOut(),
                },
                new BrushTransition
                {
                    Property = Border.BorderBrushProperty,
                    Duration = HoverAnimationDuration,
                    Easing = new CubicEaseOut(),
                },
            };
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(
            InputElement.PointerPressedEvent,
            TopLevel_OnPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        _window = _topLevel as Window;
        if (_window is not null)
        {
            _window.Deactivated += Window_OnDeactivated;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
        {
            _topLevel.RemoveHandler(InputElement.PointerPressedEvent, TopLevel_OnPointerPressed);
        }

        if (_window is not null)
        {
            _window.Deactivated -= Window_OnDeactivated;
        }

        _topLevel = null;
        _window = null;
        _animationVersion++;
        _isAnimatingClose = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (IsDropDownOpen && (e.Source is not Visual source || !IsInsidePopup(source)))
        {
            BeginCloseAnimation();
            e.Handled = true;
            return;
        }

        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_isAnimatingClose)
        {
            e.Handled = true;
            return;
        }

        base.OnPointerReleased(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsDropDownOpen)
        {
            var hasAlt = (e.KeyModifiers & KeyModifiers.Alt) == KeyModifiers.Alt;
            var togglesDropDown = (e.Key == Key.F4 && !hasAlt)
                || (hasAlt && (e.Key == Key.Up || e.Key == Key.Down));

            if (e.Key == Key.Escape || e.Key == Key.Tab || togglesDropDown)
            {
                BeginCloseAnimation();
                if (e.Key != Key.Tab)
                {
                    e.Handled = true;
                }
                return;
            }
        }

        base.OnKeyDown(e);
    }

    public override bool UpdateSelectionFromEvent(Control container, RoutedEventArgs eventArgs)
    {
        if (eventArgs.Handled)
        {
            return false;
        }

        var index = IndexFromContainer(container);
        if (index < 0 || !ShouldSelectItem(container, eventArgs))
        {
            return false;
        }

        // ComboBox's implementation closes Popup immediately after selecting. Perform
        // the same single selection here, then route closing through the animation.
        SelectedIndex = index;
        eventArgs.Handled = true;
        BeginCloseAnimation();
        return true;
    }

    private static bool ShouldSelectItem(Control container, RoutedEventArgs eventArgs)
    {
        if (eventArgs is PointerEventArgs pointerEvent)
        {
            var updateKind = pointerEvent.Properties.PointerUpdateKind;
            if (eventArgs.RoutedEvent != InputElement.PointerReleasedEvent
                || updateKind is not (PointerUpdateKind.LeftButtonReleased or PointerUpdateKind.RightButtonReleased))
            {
                return false;
            }

            var point = pointerEvent.GetPosition(container);
            return new Rect(container.Bounds.Size).Contains(point);
        }

        return eventArgs is KeyEventArgs keyEvent
            && ItemSelectionEventTriggers.ShouldTriggerSelection(container, keyEvent);
    }

    private void Popup_OnOpened(object? sender, EventArgs e)
    {
        if (_popup?.Child is not Control content)
        {
            return;
        }

        var version = ++_animationVersion;
        _isAnimatingClose = false;
        content.IsHitTestVisible = true;

        // Establish the opening pose without transitions, then ease into place.
        content.Transitions = new Transitions();
        content.Opacity = 0.0;
        var transform = new TranslateTransform { Y = -7.0 };
        content.RenderTransform = transform;
        content.RenderTransformOrigin = new RelativePoint(0.5, 0.0, RelativeUnit.Relative);
        content.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(140),
                Easing = new CubicEaseOut(),
            },
        };
        transform.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = TimeSpan.FromMilliseconds(170),
                Easing = new CubicEaseOut(),
            },
        };

        Dispatcher.UIThread.Post(() =>
        {
            if (version != _animationVersion || _popup?.IsOpen != true || _isAnimatingClose)
            {
                return;
            }

            content.Opacity = 1.0;
            transform.Y = 0.0;
        }, DispatcherPriority.Loaded);
    }

    private void BeginCloseAnimation()
    {
        if (_isAnimatingClose || !IsDropDownOpen || _popup?.IsOpen != true)
        {
            return;
        }

        if (_popup.Child is not Control content)
        {
            SetCurrentValue(IsDropDownOpenProperty, false);
            return;
        }

        _isAnimatingClose = true;
        var version = ++_animationVersion;
        content.IsHitTestVisible = false;
        content.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(125),
                Easing = new CubicEaseIn(),
            },
        };

        var transform = content.RenderTransform as TranslateTransform
            ?? new TranslateTransform();
        content.RenderTransform = transform;
        transform.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = TimeSpan.FromMilliseconds(145),
                Easing = new CubicEaseIn(),
            },
        };

        content.Opacity = 0.0;
        transform.Y = -5.0;

        DispatcherTimer.RunOnce(() =>
        {
            if (version != _animationVersion || !_isAnimatingClose)
            {
                return;
            }

            SetCurrentValue(IsDropDownOpenProperty, false);
            if (_popup?.IsOpen == true)
            {
                _popup.Close();
            }
        }, CloseAnimationDuration);
    }

    private void Popup_OnClosed(object? sender, EventArgs e)
    {
        _animationVersion++;
        _isAnimatingClose = false;

        if (_popup?.Child is Control content)
        {
            content.IsHitTestVisible = true;
            content.Transitions = new Transitions();
            content.Opacity = 0.0;
        }
    }

    private void TopLevel_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsDropDownOpen || _isAnimatingClose || e.Source is not Visual source)
        {
            return;
        }

        if (!IsInsideThisControl(source) && !IsInsidePopup(source))
        {
            BeginCloseAnimation();
        }
    }

    private void Window_OnDeactivated(object? sender, EventArgs e) => BeginCloseAnimation();

    private bool IsInsideThisControl(Visual source) =>
        ReferenceEquals(source, this) || source.GetVisualAncestors().Contains(this);

    private bool IsInsidePopup(Visual source)
    {
        if (_popup?.Child is not Visual popupContent)
        {
            return false;
        }

        return ReferenceEquals(source, popupContent)
            || source.GetVisualAncestors().Contains(popupContent);
    }
}
