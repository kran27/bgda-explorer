using JetBlackEngineLib.Data.DataContainers;

namespace WorldExplorer.TreeView;

/// <summary>
/// One DDF asset record. Children are the CLP entries the record references
/// (mesh + texture pairs and sound assets), resolved through the world's
/// AssetIndex. Hashes that have no archive entry (e.g. an asset in a CLP we
/// couldn't load) appear as dead leaves so the user still sees them.
/// </summary>
public class DdfEntityTreeViewModel : TreeViewItemViewModel
{
    private readonly World _world;
    private readonly DdfFile.EntityRecord _entity;

    public DdfFile.EntityRecord Entity => _entity;

    public DdfEntityTreeViewModel(World world, TreeViewItemViewModel parent, DdfFile.EntityRecord entity)
        : base(entity.Name, parent, true)
    {
        _world = world;
        _entity = entity;
    }

    protected override void LoadChildren()
    {
        foreach (var asset in _entity.Assets)
        {
            if (_world.AssetIndex.TryGetValue(asset.Hash, out var loc))
            {
                // Reuse the existing LMP-entry node so the main view-model's
                // OnLmpEntrySelected handles previewing without duplication.
                Children.Add(new LmpEntryTreeViewModel(_world, this, loc.Clp, loc.EntryLabel));
            }
            else
            {
                Children.Add(new DdfMissingAssetTreeViewModel(this, asset));
            }
        }
    }
}

/// <summary>Placeholder leaf for an entity asset whose CLP we couldn't load.</summary>
public class DdfMissingAssetTreeViewModel : TreeViewItemViewModel
{
    public DdfFile.EntityAsset Asset { get; }

    public DdfMissingAssetTreeViewModel(TreeViewItemViewModel parent, DdfFile.EntityAsset asset)
        : base($"({asset.Role}) {asset.Hash:X8}  [not loaded]", parent, false)
    {
        Asset = asset;
    }
}
