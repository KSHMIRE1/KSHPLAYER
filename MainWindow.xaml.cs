using System;
using System.Linq;
using KSHPLAYER.Player;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace KSHPLAYER;

/// <summary>
/// Écran d'accueil. Pendant la lecture il se cache : mpv affiche la vidéo
/// dans sa propre fenêtre sans bord. À la fin (ou touche Q), l'accueil revient.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly IntPtr _hwnd;
    private MpvPlayer? _player;
    private readonly HardwareProfile _hw = HardwareProfile.Detect();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 600));
        AppWindow.Title = Edition.Name;
        if (Edition.IsPro)
        {
            ProBadge.Visibility = Visibility.Visible;
            OpenFolderButton.Visibility = Visibility.Visible;
            EyebrowText.Text = "LECTEUR VIDÉO  ·  ÉDITION MONTEURS";
            StatusText.Text = "Glisse un rush, un film ou un dossier de séquence d'images";
        }
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "KSHPLAYER.ico"));

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        HardwareText.Text = $"{_hw.GpuName}  ·  {_hw.Label}";
        Closed += (_, _) => _player?.Dispose();

        // Lancement avec un fichier en argument (ex. « Ouvrir avec KSHPLAYER »)
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
            DispatcherQueue.TryEnqueue(() => Play(args[1]));
    }

    private static readonly string[] SequenceExts = { "exr", "dpx", "tif", "tiff", "png", "jpg", "jpeg", "tga" };

    private void Play(string path)
    {
        try
        {
            // PRO : un dossier = une séquence d'images
            if (System.IO.Directory.Exists(path))
            {
                var ext = SequenceExts
                    .Select(e => (e, n: System.IO.Directory.EnumerateFiles(path, "*." + e).Count()))
                    .OrderByDescending(x => x.n).First();
                if (ext.n < 2) { StatusText.Text = "Aucune séquence d'images dans ce dossier"; return; }
                if (_player == null)
                {
                    _player = new MpvPlayer(System.IO.Path.Combine(AppContext.BaseDirectory, "mpv"), _hw);
                    _player.Shutdown += () => DispatcherQueue.TryEnqueue(OnPlaybackClosed);
                }
                AppLog.Write($"Séquence : {path} ({ext.n} × {ext.e})");
                _player.LoadSequence(path, ext.e);
                AppWindow.Hide();
                return;
            }

            if (_player == null)
            {
                _player = new MpvPlayer(System.IO.Path.Combine(AppContext.BaseDirectory, "mpv"), _hw);
                _player.Shutdown += () => DispatcherQueue.TryEnqueue(OnPlaybackClosed);
            }
            AppLog.Write("Ouverture : " + path);
            _player.Load(path);
            AppWindow.Hide();
        }
        catch (DllNotFoundException ex)
        {
            AppLog.Write("Moteur introuvable", ex);
            StatusText.Text = "Moteur libmpv introuvable à côté de KSHPLAYER.exe";
            AppWindow.Show();
        }
        catch (Exception ex)
        {
            AppLog.Write("Lecture impossible : " + path, ex);
            StatusText.Text = "Lecture impossible : " + ex.Message;
            AppWindow.Show();
        }
    }

    private void OnPlaybackClosed()
    {
        _player?.Dispose();
        _player = null;
        StatusText.Text = Edition.IsPro ? "Glisse un rush, un film ou un dossier de séquence d'images" : "Glisse un film ou un épisode ici";
        AppWindow.Show();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        picker.PickSingleFileAsync().Completed = (op, status) =>
        {
            if (status == AsyncStatus.Completed && op.GetResults() is StorageFile file)
                DispatcherQueue.TryEnqueue(() => Play(file.Path));
        };
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        picker.PickSingleFolderAsync().Completed = (op, status) =>
        {
            if (status == AsyncStatus.Completed && op.GetResults() is StorageFolder folder)
                DispatcherQueue.TryEnqueue(() => Play(folder.Path));
        };
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = "Lire avec KSHPLAYER";
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.DataView.GetStorageItemsAsync().Completed = (op, status) =>
        {
            if (status != AsyncStatus.Completed) return;
            var item = op.GetResults().FirstOrDefault();
            if (item is StorageFile file)
                DispatcherQueue.TryEnqueue(() => Play(file.Path));
            else if (item is StorageFolder folder && Edition.IsPro)
                DispatcherQueue.TryEnqueue(() => Play(folder.Path));
        };
    }
}
