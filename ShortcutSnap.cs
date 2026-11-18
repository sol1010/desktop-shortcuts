using System;
using System.Collections.Generic;
using System.Drawing;

namespace DesktopShortcuts;

internal static class ShortcutSnap
{
    internal const double Gap = 1.5;
    private const double SnapDistance = 8;

    public static Point FindPosition(Rectangle moving, IReadOnlyList<Rectangle> neighbors,
        Rectangle workingArea, double dpiX, double dpiY)
    {
        int gapX = (int)Math.Round(Gap * dpiX, MidpointRounding.AwayFromZero);
        int gapY = (int)Math.Round(Gap * dpiY, MidpointRounding.AwayFromZero);
        double reachX = SnapDistance * dpiX;
        double reachY = SnapDistance * dpiY;
        Point best = moving.Location;
        double bestDistance = double.PositiveInfinity;

        foreach (Rectangle neighbor in neighbors)
        {
            if (moving.Top < neighbor.Bottom && moving.Bottom > neighbor.Top)
            {
                int y = Align(moving.Top, neighbor.Top, neighbor.Bottom - moving.Height, reachY);
                Consider(neighbor.Left - moving.Width - gapX, y, horizontal: true);
                Consider(neighbor.Right + gapX, y, horizontal: true);
            }

            if (moving.Left < neighbor.Right && moving.Right > neighbor.Left)
            {
                int x = Align(moving.Left, neighbor.Left, neighbor.Right - moving.Width, reachX);
                Consider(x, neighbor.Top - moving.Height - gapY, horizontal: false);
                Consider(x, neighbor.Bottom + gapY, horizontal: false);
            }
        }

        return best;

        void Consider(int x, int y, bool horizontal)
        {
            int dx = x - moving.X;
            int dy = y - moving.Y;
            if (horizontal ? Math.Abs(dx) > reachX : Math.Abs(dy) > reachY)
            {
                return;
            }

            double distance = Math.Pow(dx / dpiX, 2) + Math.Pow(dy / dpiY, 2);
            var candidate = new Rectangle(x, y, moving.Width, moving.Height);
            if (distance >= bestDistance || !workingArea.Contains(candidate))
            {
                return;
            }

            foreach (Rectangle neighbor in neighbors)
            {
                if (candidate.IntersectsWith(neighbor))
                {
                    return;
                }
            }

            bestDistance = distance;
            best = candidate.Location;
        }
    }

    private static int Align(int position, int firstEdge, int secondEdge, double reach)
    {
        int nearest = Math.Abs(position - firstEdge) <= Math.Abs(position - secondEdge)
            ? firstEdge : secondEdge;
        return Math.Abs(position - nearest) <= reach ? nearest : position;
    }
}
