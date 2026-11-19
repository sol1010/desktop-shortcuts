using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace DesktopShortcuts;

internal static class ShortcutGroupLayout
{
    internal static Rectangle Bounds(IReadOnlyList<Rectangle> tiles)
    {
        Rectangle group = tiles[0];
        for (int index = 1; index < tiles.Count; index++)
        {
            group = Rectangle.Union(group, tiles[index]);
        }
        return group;
    }

    internal static Point FitOffset(Rectangle group, Rectangle area)
    {
        return new Point(Fit(group.X, group.Width, area.Left, area.Right) - group.X,
            Fit(group.Y, group.Height, area.Top, area.Bottom) - group.Y);

        static int Fit(int coordinate, int size, int start, int end) =>
            Math.Clamp(coordinate, Math.Min(start, end - size), Math.Max(start, end - size));
    }

    internal static bool AreAttached(Rectangle first, Rectangle second, int gapX, int gapY) =>
        Contact(first, second, gapX, gapY, tolerance: 0) is not null;

    internal static Rectangle[] Resize(IReadOnlyList<Rectangle> tiles, int anchorIndex, Size size, int gapX, int gapY)
    {
        Point[] slots = FindSlots(tiles, anchorIndex, gapX, gapY);
        Rectangle anchor = tiles[anchorIndex];
        return slots.Select(slot => new Rectangle(anchor.X + slot.X * (size.Width + gapX),
            anchor.Y + slot.Y * (size.Height + gapY), size.Width, size.Height)).ToArray();
    }

    internal static Rectangle[] Reorder(IReadOnlyList<Rectangle> tiles, int source, int target, int gapX, int gapY)
    {
        Point[] slots = FindSlots(tiles, 0, gapX, gapY);
        int[] order = Enumerable.Range(0, tiles.Count).OrderBy(index => slots[index].Y)
            .ThenBy(index => slots[index].X).ToArray();
        var occupants = order.ToList();
        int destination = occupants.IndexOf(target);
        occupants.Remove(source);
        occupants.Insert(destination, source);

        var positions = new Rectangle[tiles.Count];
        if (Enumerable.Range(0, order.Length).All(index => tiles[order[index]].Size == tiles[occupants[index]].Size))
        {
            for (int index = 0; index < order.Length; index++)
            {
                positions[occupants[index]] = tiles[order[index]];
            }
            return positions;
        }

        // Use the widest and tallest icon in each column and row when the sizes differ.
        Rectangle group = Bounds(tiles);
        var columns = Offsets(Enumerable.Range(0, order.Length).Select(index =>
            (slots[order[index]].X, tiles[occupants[index]].Width)), group.Left, gapX);
        var rows = Offsets(Enumerable.Range(0, order.Length).Select(index =>
            (slots[order[index]].Y, tiles[occupants[index]].Height)), group.Top, gapY);
        for (int index = 0; index < order.Length; index++)
        {
            Point slot = slots[order[index]];
            int tile = occupants[index];
            positions[tile] = new Rectangle(new Point(columns[slot.X], rows[slot.Y]), tiles[tile].Size);
        }
        return positions;
    }

    private static Dictionary<int, int> Offsets(IEnumerable<(int Slot, int Size)> tiles, int origin, int gap)
    {
        var offsets = new Dictionary<int, int>();
        foreach (var strip in tiles.GroupBy(tile => tile.Slot).OrderBy(group => group.Key))
        {
            offsets.Add(strip.Key, origin);
            origin += strip.Max(tile => tile.Size) + gap;
        }
        return offsets;
    }

    private static Point[] FindSlots(IReadOnlyList<Rectangle> tiles, int anchorIndex, int gapX, int gapY)
    {
        var slots = new Point?[tiles.Count];
        var occupied = new HashSet<Point>();
        var pending = new Queue<int>();
        Rectangle anchor = tiles[anchorIndex];
        int[] spatialOrder = Enumerable.Range(0, tiles.Count).OrderBy(index => tiles[index].Top)
            .ThenBy(index => tiles[index].Left).ToArray();
        Assign(anchorIndex, Point.Empty);

        for (int index = 0; index < slots.Length; index++)
        {
            while (pending.TryDequeue(out int current))
            {
                Point slot = slots[current]!.Value;
                foreach (int other in spatialOrder)
                {
                    if (slots[other] is null && Contact(tiles[current], tiles[other], gapX, gapY, tolerance: 1) is Point direction)
                    {
                        var desired = new Point(slot.X + direction.X, slot.Y + direction.Y);
                        Assign(other, VacantSlot(desired, occupied, vertical: direction.X != 0));
                    }
                }
            }

            // Removing an icon can leave separated parts of the same group.
            if (slots[index] is null)
            {
                var desired = new Point(
                    (int)Math.Round((tiles[index].X - anchor.X) / (double)(anchor.Width + gapX)),
                    (int)Math.Round((tiles[index].Y - anchor.Y) / (double)(anchor.Height + gapY)));
                Assign(index, VacantSlot(desired, occupied, vertical: Math.Abs(desired.Y) > Math.Abs(desired.X)));
            }
        }

        return slots.Select(slot => slot!.Value).ToArray();

        void Assign(int index, Point slot)
        {
            slots[index] = slot;
            occupied.Add(slot);
            pending.Enqueue(index);
        }
    }

    private static Point? Contact(Rectangle first, Rectangle second, int gapX, int gapY, int tolerance)
    {
        if (first.Top < second.Bottom && first.Bottom > second.Top)
        {
            if (Math.Abs(second.Left - first.Right - gapX) <= tolerance) return new Point(1, 0);
            if (Math.Abs(first.Left - second.Right - gapX) <= tolerance) return new Point(-1, 0);
        }
        if (first.Left < second.Right && first.Right > second.Left)
        {
            if (Math.Abs(second.Top - first.Bottom - gapY) <= tolerance) return new Point(0, 1);
            if (Math.Abs(first.Top - second.Bottom - gapY) <= tolerance) return new Point(0, -1);
        }
        return null;
    }

    private static Point VacantSlot(Point desired, HashSet<Point> occupied, bool vertical)
    {
        if (!occupied.Contains(desired))
        {
            return desired;
        }

        for (int distance = 1; ; distance++)
        {
            int x = vertical ? 0 : distance;
            int y = vertical ? distance : 0;
            var first = new Point(desired.X + x, desired.Y + y);
            if (!occupied.Contains(first))
            {
                return first;
            }
            var second = new Point(desired.X - x, desired.Y - y);
            if (!occupied.Contains(second))
            {
                return second;
            }
        }
    }
}
