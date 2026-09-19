using System.Text;

namespace CsOverlay.Services
{
    /// <summary>
    /// Renders modifier + virtual-key combinations as readable strings,
    /// e.g. "Ctrl+Shift+O" or "Ctrl+Shift+F10". Dependency-free.
    /// </summary>
    internal static class HotkeyFormatter
    {
        public static string Format(bool ctrl, bool shift, bool alt, uint virtualKey)
        {
            var builder = new StringBuilder();

            if (ctrl)
            {
                builder.Append("Ctrl+");
            }

            if (shift)
            {
                builder.Append("Shift+");
            }

            if (alt)
            {
                builder.Append("Alt+");
            }

            builder.Append(VirtualKeyToText(virtualKey));
            return builder.ToString();
        }

        public static string VirtualKeyToText(uint virtualKey)
        {
            // Letters A-Z.
            if (virtualKey >= 0x41 && virtualKey <= 0x5A)
            {
                return ((char)virtualKey).ToString();
            }

            // Digits 0-9.
            if (virtualKey >= 0x30 && virtualKey <= 0x39)
            {
                return ((char)virtualKey).ToString();
            }

            // Function keys F1-F24.
            if (virtualKey >= 0x70 && virtualKey <= 0x87)
            {
                return "F" + (virtualKey - 0x70 + 1);
            }

            return "VK 0x" + virtualKey.ToString("X2");
        }
    }
}
