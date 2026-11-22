using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UniversalPlayer.App.ViewModels;

namespace UniversalPlayer.App;

public partial class MainWindow : Window
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // The GridView theme leaves the first item scrolled slightly behind the header, and the
        // auto-scroll happens after this event, so reset it again once the layout has settled.
        ResetListScroll();
        Dispatcher.BeginInvoke(new Action(ResetListScroll),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void ResetListScroll()
    {
        foreach (var list in new DependencyObject?[] { LibraryList, QueueList, ArtistList })
            if (list is not null) ScrollToTop(list);
    }

    private static void ScrollToTop(DependencyObject root)
    {
        if (root is System.Windows.Controls.ScrollViewer viewer)
        {
            viewer.ScrollToTop();
            viewer.ScrollToHorizontalOffset(0);
            return;
        }
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            ScrollToTop(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
    }

    private void LibraryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm?.SelectedTrack is not null)
            Vm.PlayTrackCommand.Execute(Vm.SelectedTrack);
    }

    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm?.SelectedQueueTrack is not null)
            Vm.PlayQueueItemCommand.Execute(Vm.SelectedQueueTrack);
    }

    private void ArtistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm?.SelectedArtist is not null)
            Vm.PlayArtistCommand.Execute(Vm.SelectedArtist);
    }

    private void BrowseLibraryRoot_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select your music folder",
            Multiselect = false
        };
        if (System.IO.Directory.Exists(Vm.LibraryRoot))
            dialog.InitialDirectory = Vm.LibraryRoot;

        if (dialog.ShowDialog(this) == true)
        {
            Vm.LibraryRoot = dialog.FolderName;
            Vm.SaveSettings();
        }
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && Vm?.MinimizeToTray == true)
            Hide();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (App.ExitRequested) return;

        if (Vm?.MinimizeToTrayOnClose == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        App.ExitRequested = true;
        Application.Current.Shutdown();
    }
}
