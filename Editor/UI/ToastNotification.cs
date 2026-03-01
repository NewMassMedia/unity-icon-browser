using UnityEngine;
using UnityEngine.UIElements;

namespace IconBrowser.UI
{
    /// <summary>
    /// Transient notification bar displayed at the bottom of an EditorWindow.
    /// Fades in, stays visible for a duration, then fades out.
    /// </summary>
    internal class ToastNotification : VisualElement
    {
        private const float DISPLAY_DURATION_MS = 4000;
        private const float FADE_DURATION_MS = 300;

        private readonly Label _label;
        IVisualElementScheduledItem _hideHandle;

        public ToastNotification()
        {
            AddToClassList("toast-notification");
            style.position = Position.Absolute;
            style.bottom = 24;
            style.left = 0;
            style.right = 0;
            style.alignItems = Align.Center;
            style.display = DisplayStyle.None;
            pickingMode = PickingMode.Ignore;

            var container = new VisualElement();
            container.AddToClassList("toast-notification__container");
            container.style.paddingLeft = 12;
            container.style.paddingRight = 12;
            container.style.paddingTop = 6;
            container.style.paddingBottom = 6;
            container.style.borderBottomLeftRadius = 4;
            container.style.borderBottomRightRadius = 4;
            container.style.borderTopLeftRadius = 4;
            container.style.borderTopRightRadius = 4;
            Add(container);

            _label = new Label();
            _label.AddToClassList("toast-notification__label");
            container.Add(_label);
        }

        /// <summary>
        /// Shows an error toast (red tint).
        /// </summary>
        public void ShowError(string message)
        {
            Show(message, new Color(0.8f, 0.2f, 0.2f, 0.9f));
        }

        /// <summary>
        /// Shows an info toast (blue tint).
        /// </summary>
        public void ShowInfo(string message)
        {
            Show(message, new Color(0.2f, 0.4f, 0.8f, 0.9f));
        }

        /// <summary>
        /// Shows a warning toast (yellow tint).
        /// </summary>
        public void ShowWarning(string message)
        {
            Show(message, new Color(0.8f, 0.6f, 0.1f, 0.9f));
        }

        private void Show(string message, Color bgColor)
        {
            _hideHandle?.Pause();

            _label.text = message;
            var container = this[0];
            container.style.backgroundColor = bgColor;

            style.display = DisplayStyle.Flex;
            style.opacity = 1f;

            _hideHandle = schedule.Execute(() =>
            {
                style.opacity = 0f;
                schedule.Execute(() => style.display = DisplayStyle.None)
                    .StartingIn((long)FADE_DURATION_MS);
            }).StartingIn((long)DISPLAY_DURATION_MS);
        }
    }
}
