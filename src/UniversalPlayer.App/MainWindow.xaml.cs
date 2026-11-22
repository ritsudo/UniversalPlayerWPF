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
        KeepWindowOnScreen();
    }

    /// <summary>Makes sure the window fits inside the working area of the screen it was centred on.</summary>
    private void KeepWindowOnScreen()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)Left + (int)(Width / 2), (int)Top + (int)(Height / 2)));
        var work = screen.WorkingArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);

        var maxWidth = work.Width / dpi.DpiScaleX;
        var maxHeight = work.Height / dpi.DpiScaleY;
        if (Width > maxWidth) Width = maxWidth;
        if (Height > maxHeight) Height = maxHeight;

        if (Left < work.Left / dpi.DpiScaleX) Left = work.Left / dpi.DpiScaleX;
        if (Top < work.Top / dpi.DpiScaleY) Top = work.Top / dpi.DpiScaleY;
        if (Left + Width > work.Right / dpi.DpiScaleX) Left = work.Right / dpi.DpiScaleX - Width;
        if (Top + Height > work.Bottom / dpi.DpiScaleY) Top = work.Bottom / dpi.DpiScaleY - Height;
    }

    private void ResetListScroll()
    {
        foreach (var list in new DependencyObject?[] { LibraryList, QueueList, ArtistList })
            if (list is not null) ScrollToTop(list);
    }

    private void DumpDiag()
    {
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var deeper = FindChild<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
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
        // Closing the window exits the player; minimise is what sends it to the tray.
        if (App.ExitRequested) return;

        Vm?.SaveSettings();
        App.ExitRequested = true;
        Application.Current.Shutdown();
    }
}
