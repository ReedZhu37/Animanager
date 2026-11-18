using Godot;

namespace Animanager.UI.Shell;

/// <summary>
/// The single source of truth for colour, radius and spacing. Nothing else in the shell
/// is allowed to write a raw hex value or a bare pixel margin.
/// </summary>
public static class Tokens
{
    // Surfaces, four steps. Depth comes from value, never from shadow.
    public static readonly Color Bg = new("0f1115");
    public static readonly Color Panel = new("161a1f");
    public static readonly Color Raised = new("1b2027");
    public static readonly Color Card = new("191e24");
    public static readonly Color CardHover = new("222831");
    public static readonly Color Sunken = new("0a0c0f");
    public static readonly Color Hover = new("232931");
    public static readonly Color Active = new("2b3340");

    public static readonly Color Line = new("232a33");
    public static readonly Color LineStrong = new("333c48");

    public static readonly Color Text = new("e7ecf2");
    public static readonly Color Text2 = new("99a4b3");
    public static readonly Color Text3 = new("69737f");

    // Accent appears only on selection, focus and the one primary action.
    public static readonly Color Accent = new("4fd1b5");
    public static readonly Color AccentDim = new("2b7a68");
    public static readonly Color AccentWash = new("14312b");
    public static readonly Color OnAccent = new("06231d");

    public static readonly Color Info = new("5b9dff");
    public static readonly Color Success = new("48c78e");
    public static readonly Color Warn = new("e8b04b");
    public static readonly Color Danger = new("e56a5f");

    public static class Radius
    {
        public const int Chip = 3;
        public const int Control = 6;
        public const int Card = 10;
        public const int Popup = 14;
        public const int Pill = 999;
    }

    public static class Size
    {
        public const int HeaderHeight = 50;
        public const int ToolbarHeight = 40;
        public const int StatusBarHeight = 28;
        public const int Row = 30;
        public const int Control = 30;
        public const int Sidebar = 210;
        public const int SidebarRail = 50;
        public const int Inspector = 292;
        public const int StageMin = 330;
        public const int StageDefault = 372;
        public const int StageUsable = 330;
    }

    public static class Space
    {
        public const int Hair = 1;
        public const int Xs = 4;
        public const int Sm = 8;
        public const int Md = 12;
        public const int Lg = 16;
        public const int Xl = 24;
    }

    public static class CardMetrics
    {
        public const int Width = 156;
        public const int Height = 174;
        public const int GapX = 12;
        public const int GapY = 14;
        public const int PadX = 16;
        public const int PadY = 16;
        public const int ThumbInset = 9;
        public const int ThumbHeight = 94;

        /// <summary>Width needed for two fully visible columns plus the outer padding.</summary>
        public static float TwoColumns => PadX * 2 + Width * 2 + GapX;
    }

    public static Color Alpha(this Color color, float alpha) => new(color.R, color.G, color.B, alpha);
}
