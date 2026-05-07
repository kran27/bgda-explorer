using JetBlackEngineLib.Data.DataContainers;
using System.Linq;

namespace WorldExplorer.TreeView;

/// <summary>
/// Root tree node for a DDF: lists every entity (asset record) in the file.
/// Each child expands to the entity's referenced CLP assets.
/// </summary>
public class DdfTreeViewModel : TreeViewItemViewModel
{
    private readonly World _world;
    private readonly DdfFile _ddf;

    public DdfFile DdfFile => _ddf;

    public DdfTreeViewModel(World world, TreeViewItemViewModel parent, DdfFile ddf)
        : base(ddf.Name, parent, true)
    {
        _world = world;
        _ddf = ddf;
    }

    protected override void LoadChildren()
    {
        // Group by entity name + record offset so multiple entities with the
        // same SDB hash (rare but possible) and ordering stay stable.
        foreach (var entity in _ddf.Entities.OrderBy(e => e.Name).ThenBy(e => e.RecordOffset))
        {
            Children.Add(new DdfEntityTreeViewModel(_world, this, entity));
        }
    }
}
