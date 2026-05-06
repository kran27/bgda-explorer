using JetBlackEngineLib.Data.DataContainers;

namespace WorldExplorer.TreeView;

public class SdbTreeViewModel : TreeViewItemViewModel
{
    public SdbFile SdbFile { get; }

    public SdbTreeViewModel(TreeViewItemViewModel parent, SdbFile sdbFile)
        : base(sdbFile.Name, parent, false)
    {
        SdbFile = sdbFile;
        SdbFile.ReadDirectory();
    }

    protected override void LoadChildren()
    {
    }
}
