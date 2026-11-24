using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace BD3DOrchestrator;

public partial class JobEditor : UserControl
{
    private JobEditorViewModel? ViewModel => this.DataContext as JobEditorViewModel;

    public JobEditor()
    {
        this.InitializeComponent();
    }

    private void UserControl_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = this.ViewModel?.IsEditable == true && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void UserControl_Drop(object sender, DragEventArgs e)
    {
        if (this.ViewModel is { IsEditable: true } vm &&
            e.Data.GetData(DataFormats.FileDrop) is string[] and [{ } droppedPath, ..])
        {
            _ = vm.LoadAsync(droppedPath);
        }
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (this.ViewModel is not { IsEditable: true } vm)
        {
            return;
        }
        OpenFileDialog fileDlg = new()
        {
            Title = "Select 3D MVC MKV File",
            Filter = "MKV Files (*.mkv)|*.mkv|All Files (*.*)|*.*"
        };
        if (fileDlg.ShowDialog() == true && fileDlg.FileName is { Length: not 0 } fileName)
        {
            _ = vm.LoadAsync(fileName);
        }
    }

    private async void BtnBrowseSteal_Click(object sender, RoutedEventArgs e)
    {
        if (this.ViewModel is not { IsEditable: true } vm)
        {
            return;
        }
        OpenFileDialog fileDlg = new()
        {
            Title = "Select MKV or MKS to Steal Tracks From",
            Filter = "MKV / MKS Files (*.mkv;*.mks)|*.mkv;*.mks|All Files (*.*)|*.*"
        };
        if (fileDlg.ShowDialog() == true && fileDlg.FileName is { Length: not 0 } fileName)
        {
            await vm.LoadStealFileAsync(fileName);
        }
    }

    private void BtnClearSteal_Click(object sender, RoutedEventArgs e)
    {
        if (this.ViewModel is { IsEditable: true } vm)
        {
            vm.ClearStealFile();
        }
    }
}
