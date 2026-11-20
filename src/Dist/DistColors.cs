using System.Drawing;

namespace RedTune.Dist
{
    // The colours of the groups. 13.11: the violet of CCD2 and the bright green of ALL came from the old standalone
    // program and clashed with the warm glass look. Now the three are one family, muted to the same depth as the
    // accent: CCD1 the app's accent (red since Red Tune: the "hot" chip for games), CCD2 a dusty blue (its cool complement, the
    // background chip), ALL an olive green (kept apart from the brighter green of «● запущена»), SKIP neutral grey.
    static class DistColors
    {
        // the pill behind the group's name
        public static Color Bg(Group g)
        {
            if (g == Group.SKIP) return Theme.Raised;
            return g == Group.CCD1 ? Theme.AccentBg : g == Group.CCD2 ? Color.FromArgb(0x26, 0x35, 0x44) : Color.FromArgb(0x2f, 0x38, 0x25);
        }

        // text on that pill
        public static Color Fg(Group g)
        {
            if (g == Group.SKIP) return Theme.Text2;
            return g == Group.CCD1 ? Theme.AccentFg : g == Group.CCD2 ? Color.FromArgb(0xae, 0xca, 0xe6) : Color.FromArgb(0xc4, 0xd4, 0xa3);
        }

        // the square marker of the group
        public static Color Dot(Group g)
        {
            if (g == Group.SKIP) return Theme.Muted;
            return g == Group.CCD1 ? Theme.Accent : g == Group.CCD2 ? Color.FromArgb(0x6f, 0x9c, 0xc8) : Color.FromArgb(0x93, 0xa8, 0x6f);
        }
    }
}
