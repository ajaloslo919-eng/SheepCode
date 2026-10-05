using System.Collections.ObjectModel;

namespace SheepCode;

// A native panel deck avoids the white system tab border in the cozy workbench.
internal sealed class PageDeck : Panel
{
    internal ObservableCollection<Panel> TabPages { get; } = [];
    private int _selectedIndex;
    internal int SelectedIndex
    {
        get => _selectedIndex;
        set { if (value < 0 || value >= TabPages.Count) return; _selectedIndex = value; ShowPage(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
    }
    internal event EventHandler? SelectedIndexChanged;
    internal PageDeck()
    {
        DoubleBuffered = true;
        TabPages.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (Panel page in e.NewItems) { page.Dock = DockStyle.Fill; page.Margin = Padding.Empty; Controls.Add(page); }
            ShowPage();
        };
    }
    private void ShowPage() { for (var i = 0; i < TabPages.Count; i++) TabPages[i].Visible = i == _selectedIndex; if (TabPages.Count > _selectedIndex) TabPages[_selectedIndex].BringToFront(); }
}
