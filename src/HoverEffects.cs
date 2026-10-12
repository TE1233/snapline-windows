using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Snapline
{
    sealed partial class Shelf
    {
        sealed class HangingMotion
        {
            internal FrameworkElement Card;
            internal RotateTransform Swing;
            internal AnimationClock Clock;
        }
        readonly List<FrameworkElement> hoverTargets = new List<FrameworkElement>();
        readonly List<HangingMotion> hangingMotions = new List<HangingMotion>();
        bool hoverEffectsPaused;
        int removalPreviewHolds;
        DispatcherTimer previewResumeTimer;
        FrameworkElement previewResumeTarget;
        DateTime previewResumeAt;
        bool previewResumePending;
        bool CanShowHoverPreview { get { return expanded && !opening && !collapsing && !hoverEffectsPaused && !dialogOpen && !capturePending && !dragging && !shuttingDown; } }
        internal bool HoverPreviewAllowed { get { return CanShowHoverPreview && removalPreviewHolds == 0 && !previewResumePending; } }
        bool HoverServiceEnabled { get { return !hoverEffectsPaused && removalPreviewHolds == 0 && !previewResumePending && !shuttingDown; } }
        void RegisterHoverPreview(FrameworkElement target)
        {
            hoverTargets.Add(target);
            ToolTipService.SetIsEnabled(target, HoverServiceEnabled);
            target.ToolTipOpening += delegate(object sender, ToolTipEventArgs e) { if (!HoverPreviewAllowed) e.Handled = true; };
        }
        void UpdateHoverPreviewAvailability()
        {
            foreach (var target in hoverTargets) ToolTipService.SetIsEnabled(target, HoverServiceEnabled);
        }
        void CancelPreviewResume()
        {
            if (previewResumeTimer != null) previewResumeTimer.Stop();
            previewResumePending = false; previewResumeTarget = null;
        }
        void BeginRemovalPreviewHold()
        {
            removalPreviewHolds++;
            CancelPreviewResume(); UpdateHoverPreviewAvailability();
            CloseImagePreview(); CloseNotePreview(); if (helpTip != null) helpTip.IsOpen = false;
        }
        FrameworkElement HoverTargetUnderPointer()
        {
            if (!IsLoaded || !IsVisible || PresentationSource.FromVisual(this) == null) return null;
            var point = PointerPosition(); if (!point.HasValue) return null;
            DependencyObject hit;
            try { hit = InputHitTest(PointFromScreen(new Point(point.Value.X, point.Value.Y))) as DependencyObject; }
            catch (InvalidOperationException) { return null; } // Owner teardown can detach the presentation source before child Closed fires.
            while (hit != null)
            {
                var target = hit as FrameworkElement;
                if (target != null && hoverTargets.Contains(target)) return target;
                hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
            }
            return null;
        }
        void EndRemovalPreviewHold()
        {
            removalPreviewHolds--;
            if (removalPreviewHolds > 0) return;
            var target = CanShowHoverPreview ? HoverTargetUnderPointer() : null;
            if (target != null)
            {
                // WPF does not always re-open a tooltip for an unmoved pointer after re-enabling it.
                // Start a fresh hover delay only when the last real falling window has closed.
                previewResumePending = true; previewResumeTarget = target;
                previewResumeAt = DateTime.Now.AddMilliseconds(ToolTipService.GetInitialShowDelay(target));
                if (previewResumeTimer == null)
                {
                    previewResumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                    previewResumeTimer.Tick += ResumePreviewAfterRemoval;
                }
                previewResumeTimer.Start();
            }
            UpdateHoverPreviewAvailability();
        }
        void ResumePreviewAfterRemoval(object sender, EventArgs e)
        {
            if (!CanShowHoverPreview || removalPreviewHolds > 0)
            { CancelPreviewResume(); UpdateHoverPreviewAvailability(); return; }
            var target = HoverTargetUnderPointer();
            if (target == null)
            { CancelPreviewResume(); UpdateHoverPreviewAvailability(); return; }
            if (target != previewResumeTarget || Mouse.LeftButton == MouseButtonState.Pressed)
            {
                previewResumeTarget = target;
                previewResumeAt = DateTime.Now.AddMilliseconds(ToolTipService.GetInitialShowDelay(target));
                return;
            }
            if (DateTime.Now < previewResumeAt) return;
            CancelPreviewResume(); UpdateHoverPreviewAvailability();
            var tip = target.ToolTip as ToolTip;
            if (tip != null && HoverPreviewAllowed) tip.IsOpen = true;
        }
        void RegisterHangingMotion(FrameworkElement card, RotateTransform swing, DoubleAnimation breeze)
        {
            card.CacheMode = new BitmapCache();
            hangingMotions.Add(new HangingMotion { Card = card, Swing = swing });
            SetHangingAnimation(swing, breeze);
        }
        void SetHangingAnimation(RotateTransform swing, DoubleAnimation animation)
        {
            var motion = hangingMotions.Find(x => Object.ReferenceEquals(x.Swing, swing));
            if (motion == null) return;
            var timeline = animation.Clone();
            if (!timeline.From.HasValue) timeline.From = swing.Angle;
            var previous = motion.Clock;
            motion.Clock = (AnimationClock)timeline.CreateClock(true);
            swing.ApplyAnimationClock(RotateTransform.AngleProperty, motion.Clock);
            if (previous != null) previous.Controller.Remove();
            if (hoverEffectsPaused) motion.Clock.Controller.Pause();
        }
        void PauseHoverEffects(bool pause)
        {
            if (hoverEffectsPaused == pause) return;
            hoverEffectsPaused = pause;
            if (pause) CancelPreviewResume();
            UpdateHoverPreviewAvailability();
            foreach (var motion in hangingMotions)
            {
                if (motion.Clock == null) continue;
                if (pause) motion.Clock.Controller.Pause();
                else motion.Clock.Controller.Resume();
                var remove = FlightDecorations(motion.Card).FirstOrDefault(x => Object.Equals(x.Tag, "DeleteButton"));
                if (remove != null)
                {
                    bool show = !pause && motion.Card.IsMouseOver;
                    remove.IsHitTestVisible = show;
                    remove.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(show ? 1 : 0, System.TimeSpan.FromMilliseconds(120)));
                }
            }
        }
        void ClearHoverEffects()
        {
            CancelPreviewResume();
            foreach (var motion in hangingMotions)
            {
                motion.Swing.ApplyAnimationClock(RotateTransform.AngleProperty, null);
                if (motion.Clock != null) motion.Clock.Controller.Remove();
            }
            hangingMotions.Clear(); hoverTargets.Clear();
        }
    }
}
