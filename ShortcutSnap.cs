using System;
using System.Collections.Generic;
using System.Drawing;

namespace DesktopShortcuts;

internal readonly record struct ShortcutSnapMatch(Point Offset, int NeighborIndex);

internal static class ShortcutSnap
{
    internal const double Gap = 1.5;
    private const double SnapDistance = 8;

    public static Point FindPosition(Rectangle moving, IReadOnlyList<Rectangle> neighbors,
        Rectangle workingArea, double dpiX, double dpiY)
    {
        Point offset = FindMatch([moving], neighbors, workingArea, dpiX, dpiY)?.Offset ?? Point.Empty;
        return new Point(moving.X + offset.X, moving.Y + offset.Y);
    }

    internal static ShortcutSnapMatch? FindMatch(IReadOnlyList<Rectangle> moving, IReadOnlyList<Rectangle> neighbors,
        Rectangle workingArea, double dpiX, double dpiY)
    {
        int gapX = PixelGap(dpiX);
        int gapY = PixelGap(dpiY);
        double reachX = SnapDistance * dpiX;
        double reachY = SnapDistance * dpiY;
        Rectangle group = ShortcutGroupLayout.Bounds(moving);
        ShortcutSnapMatch? best = null;
        double bestDistance = double.PositiveInfinity;

        foreach (Rectangle tile in moving)
        {
            for (int index = 0; index < neighbors.Count; index++)
            {
                Rectangle neighbor = neighbors[index];
                if (tile.Top < neighbor.Bottom && tile.Bottom > neighbor.Top)
                {
                    int y = Align(tile.Top, neighbor.Top, neighbor.Bottom - tile.Height, reachY) - tile.Y;
                    Consider(neighbor.Left - tile.Right - gapX, y, horizontal: true, index);
                    Consider(neighbor.Right + gapX - tile.Left, y, horizontal: true, index);
                }

                if (tile.Left < neighbor.Right && tile.Right > neighbor.Left)
                {
                    int x = Align(tile.Left, neighbor.Left, neighbor.Right - tile.Width, reachX) - tile.X;
                    Consider(x, neighbor.Top - tile.Bottom - gapY, horizontal: false, index);
                    Consider(x, neighbor.Bottom + gapY - tile.Top, horizontal: false, index);
                }
            }
        }

        return best;

        void Consider(int dx, int dy, bool horizontal, int index)
        {
            if (horizontal ? Math.Abs(dx) > reachX : Math.Abs(dy) > reachY)
            {
                return;
            }

            double distance = Math.Pow(dx / dpiX, 2) + Math.Pow(dy / dpiY, 2);
            var translatedGroup = group;
            translatedGroup.Offset(dx, dy);
            if (distance >= bestDistance || !workingArea.Contains(translatedGroup))
            {
                return;
            }

            foreach (Rectangle tile in moving)
            {
                Rectangle candidate = tile;
                candidate.Offset(dx, dy);
                foreach (Rectangle neighbor in neighbors)
                {
                    if (candidate.IntersectsWith(neighbor))
                    {
                        return;
                    }
                }
            }

            bestDistance = distance;
            best = new ShortcutSnapMatch(new Point(dx, dy), index);
        }
    }

    internal static int PixelGap(double dpi) => (int)Math.Round(Gap * dpi, MidpointRounding.AwayFromZero);

    private static int Align(int position, int firstEdge, int secondEdge, double reach)
    {
        int nearest = Math.Abs(position - firstEdge) <= Math.Abs(position - secondEdge)
            ? firstEdge : secondEdge;
        return Math.Abs(position - nearest) <= reach ? nearest : position;
    }
}
