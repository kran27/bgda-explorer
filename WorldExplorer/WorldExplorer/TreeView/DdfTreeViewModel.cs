using JetBlackEngineLib.Data.DataContainers;
using System.Linq;

namespace WorldExplorer.TreeView;

/// <summary>
/// Helpers for grouping a DDF's entities by category in the tree. Exposed as
/// static so the World root node can host the category folders directly,
/// without an intermediate DDF wrapper that just duplicates the file name.
/// </summary>
public static class DdfTreeBuilder
{
    /// <summary>Add one folder per populated category to <paramref name="parent"/>.</summary>
    public static void AddCategoryFolders(TreeViewItemViewModel parent, World world, DdfFile ddf)
    {
        var byCat = ddf.Entities
            .GroupBy(e => e.CategoryCode)
            .OrderBy(g => g.Key);
        foreach (var group in byCat)
        {
            var folder = new DdfCategoryTreeViewModel(parent, CategoryLabel(group.Key), group.Key, group.Count());
            foreach (var entity in group.OrderBy(e => e.Name).ThenBy(e => e.RecordOffset))
            {
                folder.Children.Add(new DdfEntityTreeViewModel(world, folder, entity));
            }
            parent.Children.Add(folder);
        }
    }

    /// <summary>
    /// Cat code → human-friendly label. Categories we haven't fully identified
    /// semantically render as "Cat N".
    /// </summary>
    public static string CategoryLabel(int cat) => cat switch
    {
        0 => "Cat 0 — Characters",
        1 => "Cat 1 — Props",
        2 => "Cat 2 — Weapons",
        3 => "Cat 3 — Armor / Pickups",
        4 => "Cat 4 — Projectiles",
        5 => "Cat 5 — Particle Emitters",
        6 => "Cat 6 — Effects",
        8 => "Cat 8 — Level",
        9 => "Cat 9 — AI Behaviors",
        10 => "Cat 10 — Dynamic Lights",
        11 => "Cat 11 — Beam Effects",
        12 => "Cat 12 — Debris",
        13 => "Cat 13 — Mines / Traps",
        _ => $"Cat {cat}",
    };
}

/// <summary>
/// Intermediate tree node grouping entities of one category. Inert — selecting
/// it doesn't trigger any rendering or log output; it's just a folder.
/// </summary>
public class DdfCategoryTreeViewModel : TreeViewItemViewModel
{
    public int CategoryCode { get; }

    public DdfCategoryTreeViewModel(TreeViewItemViewModel parent, string label, int categoryCode, int count)
        : base($"{label}  ({count})", parent, false)
    {
        CategoryCode = categoryCode;
    }
}
