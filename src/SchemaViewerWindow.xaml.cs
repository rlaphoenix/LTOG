using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LTOG;

public sealed partial class SchemaViewerWindow : Window
{
    private readonly SchemaSnapshot _snapshot;

    public SchemaViewerWindow(string schemaPath)
    {
        _snapshot = SchemaParser.Parse(new FileInfo(schemaPath));

        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 900));
        Title = $"LTOG: viewing LTFS Index {_snapshot.Path}";

        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(ico))
                AppWindow.SetIcon(ico);
        }
        catch { }

        PopulateCartridgeProperties();
        SchemaTree.RootNodes.Add(BuildTreeNode(_snapshot.Root, true));
        ShowNode(_snapshot.Root);
    }

    private void PopulateCartridgeProperties()
    {
        VolumeUuidText.Text = Blank(_snapshot.VolumeUuid);
        FormatVersionText.Text = Blank(_snapshot.FormatVersion);
        CreatorText.Text = Blank(_snapshot.Creator);
        GenerationText.Text = Blank(_snapshot.GenerationNumber);
        UpdatedText.Text = Blank(_snapshot.UpdatedText);
        HighestFileIdText.Text = Blank(_snapshot.HighestFileUid);
        PolicyOverrideText.Text = Blank(_snapshot.PolicyOverrideText);
        VolumeLockText.Text = Blank(_snapshot.VolumeLockState);
    }

    private static TreeViewNode BuildTreeNode(SchemaNode node, bool expanded = false)
    {
        var treeNode = new TreeViewNode
        {
            Content = node,
            IsExpanded = expanded,
            HasUnrealizedChildren = node.Children.Count > 0,
        };
        if (expanded)
            PopulateTreeNode(treeNode);
        return treeNode;
    }

    private void SchemaTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if ((args.InvokedItem as SchemaNode ?? (args.InvokedItem as TreeViewNode)?.Content) is SchemaNode node)
        {
            ShowNode(node);
            ToggleTreeNode(node);
        }
    }

    private void ToggleTreeNode(SchemaNode node)
    {
        if (node.Children.Count == 0) return;
        var treeNode = FindTreeNode(SchemaTree.RootNodes, node);
        if (treeNode == null) return;
        if (!treeNode.IsExpanded)
            PopulateTreeNode(treeNode);
        treeNode.IsExpanded = !treeNode.IsExpanded;
    }

    private static TreeViewNode? FindTreeNode(IEnumerable<TreeViewNode> nodes, SchemaNode target)
    {
        foreach (var treeNode in nodes)
        {
            if (ReferenceEquals(treeNode.Content, target))
                return treeNode;
            var child = FindTreeNode(treeNode.Children, target);
            if (child != null)
                return child;
        }
        return null;
    }

    private static void PopulateTreeNode(TreeViewNode treeNode)
    {
        if (!treeNode.HasUnrealizedChildren || treeNode.Content is not SchemaNode node)
            return;
        foreach (var child in node.Children)
            treeNode.Children.Add(BuildTreeNode(child));
        treeNode.HasUnrealizedChildren = false;
    }

    private void SchemaTree_Expanding(object sender, TreeViewExpandingEventArgs args) =>
        PopulateTreeNode(args.Node);

    private void ShowNode(SchemaNode node)
    {
        SelectedPathBox.Text = string.IsNullOrWhiteSpace(node.Path) ? "\\" : node.Path;
        LengthText.Text = node.IsDirectory
            ? $"{node.Children.Count:N0} item{(node.Children.Count == 1 ? "" : "s")}"
            : node.Length.HasValue ? SchemaSnapshot.FormatBytes(node.Length.Value) : "Not specified";
        FileIdText.Text = Blank(node.FileUid);
        ReadOnlyText.Text = node.ReadOnly ? "true" : "false";
        CreatedText.Text = Blank(node.CreationTime);
        ModifiedText.Text = Blank(node.ModifyTime);
        AccessedText.Text = Blank(node.AccessTime);
        BackupText.Text = Blank(node.BackupTime);
        LocationText.Text = node.IsDirectory ? "Not specified" : node.FirstLocationText;
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(SelectedPathBox.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "Not specified" : value;
}
