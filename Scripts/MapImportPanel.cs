using System;
using System.IO;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace cielo.Scripts;

internal static class MapImportPanel
{
    private const string LayerName = "MapImportPanelLayer";

    public static void EnsureInstalled()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return;
        }

        var root = tree.Root;
        if (root.GetNodeOrNull<MapImportPanelLayer>(LayerName) is not null)
        {
            return;
        }

        root.CallDeferred(Node.MethodName.AddChild, new MapImportPanelLayer
        {
            Name = LayerName,
        });
        ModLog.Info("MapImportPanel: installed root canvas layer.");
    }

    public static void Show(NMapScreen? mapScreen, string? status = null)
    {
        EnsureInstalled();
        if (FindLayer() is { } layer)
        {
            layer.ShowForMap(mapScreen, status);
        }
    }

    public static void Hide()
    {
        if (FindLayer() is { } layer)
        {
            layer.HidePanel();
        }
    }

    public static void TryRefresh(NMapScreen? mapScreen, string? status = null)
    {
        if (FindLayer() is { } layer)
        {
            layer.Refresh(status);
        }
    }

    private static MapImportPanelLayer? FindLayer()
    {
        return Engine.GetMainLoop() is SceneTree tree
            ? tree.Root.GetNodeOrNull<MapImportPanelLayer>(LayerName)
            : null;
    }

    public static void NotifyHotkeyStartPlayback()
    {
        var r = MapStrokeInputPlayback.TryStart(NMapScreen.Instance);
        TryRefresh(NMapScreen.Instance, r.Message);
    }

    public static void NotifyHotkeyStopPlayback()
    {
        var r = MapStrokeInputPlayback.TryStop(saveProgress: true);
        TryRefresh(NMapScreen.Instance, r.Message);
    }
}

internal partial class MapImportPanelLayer : CanvasLayer
{
    private static readonly Color BgPanel = new(0.11f, 0.11f, 0.12f, 0.92f);
    private static readonly Color BgSection = new(1f, 1f, 1f, 0.06f);
    private static readonly Color SeparatorColor = new(1f, 1f, 1f, 0.08f);
    private static readonly Color TextPrimary = new(1f, 1f, 1f, 0.92f);
    private static readonly Color TextSecondary = new(0.92f, 0.92f, 0.96f, 0.55f);
    private static readonly Color TextTertiary = new(0.92f, 0.92f, 0.96f, 0.32f);
    private static readonly Color TextSuccess = new(0.45f, 0.88f, 0.58f, 0.98f);
    private const int RadiusPanel = 16;
    private const int RadiusSection = 10;
    private const int FontTitle = 15;
    private const int FontBody = 13;
    private const int FontCaption = 11;
    private const int FontBtnSmall = 12;

    [Flags]
    private enum Edge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    private const float GrabZone = 8f;
    private const float MinW = 300f;
    private const float MaxW = 720f;

    private PanelContainer _panel = null!;
    private Label _imageNameLabel = null!;
    private Label? _folderPathLabel;
    private Label _scaleValueLabel = null!;
    private PanelBtn _previewButton = null!;
    private Label _statusLabel = null!;
    private Label _detailValueLabel = null!;
    private Label _maxLinesValueLabel = null!;
    private Label _edgeValueLabel = null!;
    private Label _minLenValueLabel = null!;
    private Label _morphCloseValueLabel = null!;
    private Label _joinValueLabel = null!;
    private Label _cannyContrastValueLabel = null!;
    private Label _pointsPerFrameValueLabel = null!;
    private OptionButton _extractAlgorithmOption = null!;
    private bool _syncingExtractAlgorithm;
    private VBoxContainer _advancedSection = null!;
    private PanelBtn _advancedToggleBtn = null!;
    private PanelBtn _importButton = null!;
    private Label _playbackProgressLabel = null!;
    private PanelBtn? _deleteImageButton;
    private string _statusText = "将图片放入图库文件夹后点击导入。";
    private LocalImageServer? _server;

    private bool _shownForMap;
    // 不再模拟指针后面板不需要隐藏——可在绘制中实时看进度。
    private bool _isDragging;
    private bool _isResizing;
    private Vector2 _dragOffset;
    private Vector2 _resizeStartMouse;
    private Vector2 _resizeStartPos;
    private float _resizeStartWidth;
    private Edge _resizeEdges;

    public MapImportPanelLayer()
    {
        ProcessMode = ProcessModeEnum.Always;
        Layer = 110;
        Visible = false;
    }

    public override void _Ready()
    {
        BuildUi();
        MapStrokeInputPlayback.PlaybackFinished += OnStrokePlaybackFinished;
        if (MapImportLibrary.IsMobile)
            StartUploadServer();
        Refresh();
        Visible = false;
        _panel.ResetSize();
        _panel.CallDeferred(Control.MethodName.ResetSize);
    }

    private void OnStrokePlaybackFinished()
    {
        string msg;
        if (MapStrokeInputPlayback.LastSessionEndedNaturally)
        {
            msg = "绘制已完成。";
        }
        else if (MapStrokeInputPlayback.LastSessionAborted)
        {
            msg = "绘制已中断。";
        }
        else if (MapStrokeInputPlayback.LastSessionStoppedManually)
        {
            msg = "已停止；进度已保存时可点「开始绘制」续画。";
        }
        else
        {
            msg = "绘制已结束。";
        }

        Refresh(msg);
    }

    private void StartUploadServer()
    {
        try
        {
            _server = new LocalImageServer(MapImportLibrary.GetImportFolder());
            if (_server.TryStart())
                ModLog.Info($"MapImportPanel: upload server at {_server.Url}");
            else
                ModLog.Info("MapImportPanel: upload server failed to start");
        }
        catch (Exception ex)
        {
            ModLog.Info($"MapImportPanel: upload server error: {ex.Message}");
        }
    }

    public void ShowForMap(NMapScreen? mapScreen, string? status = null)
    {
        _shownForMap = mapScreen is not null && mapScreen.IsOpen && mapScreen.IsVisibleInTree();
        Refresh(status);
        ApplyPanelVisibility();
        _panel.ResetSize();
        _panel.CallDeferred(Control.MethodName.ResetSize);
        ModLog.Info($"MapImportPanel: visibility set to {Visible}.");
    }

    // SetPlaybackSuppressesPanel removed: no longer needed without mouse simulation.

    public void HidePanel()
    {
        _shownForMap = false;
        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (_server is not null && _server.UploadedFiles.TryDequeue(out var uploaded))
        {
            MapImportLibrary.RefreshCache();
            var images = MapImportLibrary.CachedImages;
            var dest = images.FirstOrDefault(p =>
                Path.GetFileName(p).Equals(uploaded, StringComparison.OrdinalIgnoreCase));
            if (dest is not null)
            {
                var s = MapPaintSettings.Load();
                s.ImagePath = dest;
                s.Save();
            }
            Refresh($"已通过浏览器上传: {uploaded}");
        }

        ApplyPanelVisibility();

        if (MapStrokeInputPlayback.IsActive && Visible)
        {
            UpdatePlaybackProgressUi();
        }
    }

    private void ApplyPanelVisibility()
    {
        if (!_shownForMap)
        {
            Visible = false;
            return;
        }

        var mapScreen = NMapScreen.Instance;
        if (mapScreen is null || !mapScreen.IsOpen || !mapScreen.IsVisibleInTree())
        {
            Visible = false;
            return;
        }

        // 不再隐藏面板，绘制中可实时看进度。

        bool overlayActive = false;
        try
        {
            var capstone = NCapstoneContainer.Instance;
            if (capstone is not null && capstone.InUse)
                overlayActive = true;
        }
        catch
        {
            // NRun.Instance may not exist yet
        }

        Visible = !overlayActive;
    }

    public void Refresh(string? status = null)
    {
        if (!string.IsNullOrWhiteSpace(status))
            _statusText = status;

        var settings = MapPaintSettings.Load();
        var selectedPath = MapImportLibrary.ResolveSelectedImage(settings);

        var images = MapImportLibrary.CachedImages;
        var idx = MapImportLibrary.FindCurrentIndex(selectedPath);
        if (selectedPath is not null && idx >= 0)
            _imageNameLabel.Text = $"{Path.GetFileName(selectedPath)}  ({idx + 1}/{images.Length})";
        else if (selectedPath is not null)
            _imageNameLabel.Text = Path.GetFileName(selectedPath);
        else if (images.Length > 0)
            _imageNameLabel.Text = $"共 {images.Length} 张图片可选";
        else
            _imageNameLabel.Text = "图库为空";

        _scaleValueLabel.Text = $"{(int)(settings.DrawScale * 100)}%";
        _detailValueLabel.Text = $"{settings.MaxImageSize}";
        _maxLinesValueLabel.Text = $"{settings.MaxStrokes}";
        _edgeValueLabel.Text = $"{settings.DarkThreshold:F2}";
        _minLenValueLabel.Text = $"{settings.MinStrokeLength}";
        _morphCloseValueLabel.Text = $"{settings.MorphCloseIterations}";
        _joinValueLabel.Text = $"{settings.StrokeJoinPixels}";
        _cannyContrastValueLabel.Text = $"{(int)Math.Round(settings.CannyContrast * 100f)}%";
        _pointsPerFrameValueLabel.Text = $"{settings.PointsPerFrame}";
        _syncingExtractAlgorithm = true;
        try
        {
            var algIdx = string.Equals(settings.ExtractAlgorithm, "canny", StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1;
            if (_extractAlgorithmOption.Selected != algIdx)
            {
                _extractAlgorithmOption.Select(algIdx);
            }
        }
        finally
        {
            _syncingExtractAlgorithm = false;
        }

        _statusLabel.Text = _statusText;
        var mapOk = NMapScreen.Instance is not null && NMapScreen.Instance.IsOpen;
        var busy = MapStrokeInputPlayback.IsActive;
        _importButton.Text = busy ? "停止绘制（F5）" : "开始绘制（F5）";
        _importButton.SetDisabled(selectedPath is null || !mapOk);
        _previewButton.SetDisabled(selectedPath is null || !mapOk || busy);
        _deleteImageButton?.SetDisabled(selectedPath is null || busy);
        UpdatePlaybackProgressUi();
        SyncAdvancedPanelUi(settings);
    }


    private void UpdatePlaybackProgressUi()
    {
        if (MapStrokeInputPlayback.IsActive)
        {
            var p = MapStrokeInputPlayback.GetApproximateProgressPercent();
            _playbackProgressLabel.Text = $"约 {p}%";
            _playbackProgressLabel.TooltipText = "按各笔折线总长估算的约略完成比例。";
            _playbackProgressLabel.AddThemeColorOverride("font_color", TextSecondary);
            return;
        }

        if (MapStrokeInputPlayback.LastSessionEndedNaturally)
        {
            _playbackProgressLabel.Text = "完成";
            _playbackProgressLabel.TooltipText = "上一段已自然画完。";
            _playbackProgressLabel.AddThemeColorOverride("font_color", TextSuccess);
            return;
        }

        if (MapStrokeInputPlayback.LastSessionAborted)
        {
            _playbackProgressLabel.Text = "已中断";
            _playbackProgressLabel.TooltipText = "上一段因地图不可用等原因中断。";
            _playbackProgressLabel.AddThemeColorOverride("font_color", TextTertiary);
            return;
        }

        if (MapStrokeInputPlayback.LastSessionStoppedManually)
        {
            _playbackProgressLabel.Text = "已停止";
            _playbackProgressLabel.TooltipText = "上一段由 F5 或按钮手动停止；若已保存进度可续画。";
            _playbackProgressLabel.AddThemeColorOverride("font_color", TextSecondary);
            return;
        }

        _playbackProgressLabel.Text = "—";
        _playbackProgressLabel.TooltipText = "开始绘制后显示约略进度。";
        _playbackProgressLabel.AddThemeColorOverride("font_color", TextTertiary);
    }

    private void SyncAdvancedPanelUi(MapPaintSettings settings)
    {
        var expanded = settings.ShowAdvancedPanel;
        _advancedSection.Visible = expanded;
        _advancedToggleBtn.Text = expanded ? "高级设置 ▲" : "高级设置 ▼";
        // 折叠后 PanelContainer 可能仍保留展开时的高度，需按子控件最小尺寸收回
        _panel.ResetSize();
        _panel.CallDeferred(Control.MethodName.ResetSize);
    }

    private void BuildUi()
    {
        var cfg = MapPaintSettings.Load();
        var startX = float.IsNaN(cfg.WindowX) ? 24f : cfg.WindowX;
        var startY = float.IsNaN(cfg.WindowY) ? 72f : cfg.WindowY;
        var startW = float.IsNaN(cfg.WindowWidth) ? 450f : cfg.WindowWidth;

        _panel = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Stop,
            ProcessMode = ProcessModeEnum.Always,
            Position = new Vector2(startX, startY),
            CustomMinimumSize = new Vector2(Math.Clamp(startW, MinW, MaxW), 0),
        };
        _panel.AddThemeStyleboxOverride("panel", MakeStyle(BgPanel, default, 0, RadiusPanel, 20, 18));
        _panel.GuiInput += OnPanelGuiInput;
        AddChild(_panel);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        root.MouseFilter = Control.MouseFilterEnum.Ignore;
        _panel.AddChild(root);

        BuildHeaderSection(root);
        root.AddChild(MakeDivider());
        BuildSelectorSection(root);
        root.AddChild(MakeDivider());
        BuildActionsSection(root);
        root.AddChild(MakeDivider());
        BuildBasicParamsSection(root);
        _advancedToggleBtn = MakeTextBtn("高级设置 ▼", OnToggleAdvancedPanel);
        _advancedToggleBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _advancedToggleBtn.TooltipText = "精度、线条数量、边缘细节、位置微调等。";
        root.AddChild(_advancedToggleBtn);

        _advancedSection = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _advancedSection.AddThemeConstantOverride("separation", 6);
        _advancedSection.Visible = cfg.ShowAdvancedPanel;
        root.AddChild(_advancedSection);
        BuildAdvancedParamsSection(_advancedSection);
        BuildAdvancedTransformSection(_advancedSection);

        if (cfg.ShowAdvancedPanel)
        {
            _advancedToggleBtn.Text = "高级设置 ▲";
        }

        root.AddChild(MakeDivider());
        BuildFooterSection(root);
    }

    private void OnToggleAdvancedPanel()
    {
        var s = MapPaintSettings.Load();
        s.ShowAdvancedPanel = !s.ShowAdvancedPanel;
        s.Save();
        SyncAdvancedPanelUi(s);
    }

    private void BuildHeaderSection(Control parent)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        parent.AddChild(row);

        var title = new Label
        {
            Text = "地图绘制 v1.1.0",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        title.AddThemeFontSizeOverride("font_size", FontTitle);
        title.AddThemeColorOverride("font_color", TextPrimary);
        row.AddChild(title);

        var badge = new Label
        {
            Text = "by cielo",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.AddThemeFontSizeOverride("font_size", 10);
        badge.AddThemeColorOverride("font_color", TextTertiary);
        row.AddChild(badge);
    }

    private void BuildSelectorSection(Control parent)
    {
        var outer = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        outer.AddThemeConstantOverride("separation", 6);
        parent.AddChild(outer);

        var row1 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row1.AddThemeConstantOverride("separation", 6);
        outer.AddChild(row1);

        row1.AddChild(MakeStepperBtn("\u25C0", OnPrevImage));

        _imageNameLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipText = true,
        };
        _imageNameLabel.AddThemeFontSizeOverride("font_size", FontBody);
        _imageNameLabel.AddThemeColorOverride("font_color", TextPrimary);
        row1.AddChild(_imageNameLabel);

        row1.AddChild(MakeStepperBtn("\u25B6", OnNextImage));

        if (!MapImportLibrary.IsMobile)
        {
            var row2 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            row2.AddThemeConstantOverride("separation", 6);
            outer.AddChild(row2);

            _folderPathLabel = new Label
            {
                Text = $"图库: {MapImportLibrary.GetImportFolder()}",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ClipText = true,
            };
            _folderPathLabel.AddThemeFontSizeOverride("font_size", 10);
            _folderPathLabel.AddThemeColorOverride("font_color", TextTertiary);
            row2.AddChild(_folderPathLabel);

            row2.AddChild(MakeTextBtn("打开", OnOpenFolder));
            row2.AddChild(MakeTextBtn("刷新", OnRefreshImages));
        }
    }

    private void BuildActionsSection(Control parent)
    {
        var outer = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        outer.AddThemeConstantOverride("separation", 6);
        parent.AddChild(outer);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        outer.AddChild(row);

        _importButton = MakePillBtn("开始绘制（F5）", OnDrawAction, true);
        _importButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (MapImportLibrary.IsMobile)
        {
            _importButton.CustomMinimumSize = new Vector2(0, 48);
        }

        row.AddChild(_importButton);

        _playbackProgressLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = "—",
        };
        _playbackProgressLabel.AddThemeFontSizeOverride("font_size", FontBtnSmall);
        _playbackProgressLabel.AddThemeColorOverride("font_color", TextTertiary);
        _playbackProgressLabel.CustomMinimumSize = new Vector2(MapImportLibrary.IsMobile ? 60 : 100, 0);
        row.AddChild(_playbackProgressLabel);

        if (MapImportLibrary.IsMobile)
        {
            var uploadDeleteRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            uploadDeleteRow.AddThemeConstantOverride("separation", 6);
            outer.AddChild(uploadDeleteRow);

            var uploadBtn = MakeGreenPillBtn("上传图片", OnOpenUploadPage);
            uploadBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            uploadBtn.CustomMinimumSize = new Vector2(0, 44);
            uploadDeleteRow.AddChild(uploadBtn);

            _deleteImageButton = MakeRedPillBtn("删除图片", OnDeleteSelectedImage);
            _deleteImageButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _deleteImageButton.CustomMinimumSize = new Vector2(0, 44);
            uploadDeleteRow.AddChild(_deleteImageButton);
        }
    }

    /// <summary>常用：算法、画布缩放。</summary>
    private void BuildBasicParamsSection(Control parent)
    {
        var outer = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        outer.AddThemeConstantOverride("separation", 6);
        parent.AddChild(outer);

        var rowAlg = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        rowAlg.AddThemeConstantOverride("separation", 0);
        outer.AddChild(rowAlg);

        var algCaption = MakeCaptionLabel("算法");
        algCaption.CustomMinimumSize = new Vector2(44, 0);
        algCaption.TooltipText = "提取线稿的方式：骨架偏手绘感，Canny 偏轮廓感。";
        rowAlg.AddChild(algCaption);

        _extractAlgorithmOption = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = "骨架：XDoG 或亮度阈值 → 可选闭运算 → 细化 → 笔画。\nCanny：边缘检测 → 可选闭运算 → 笔画。",
        };
        _extractAlgorithmOption.AddItem("Canny", 0);
        _extractAlgorithmOption.AddItem("骨架 XDoG", 1);
        _extractAlgorithmOption.ItemSelected += OnExtractAlgorithmSelected;
        _extractAlgorithmOption.AddThemeFontSizeOverride("font_size", FontBtnSmall);
        rowAlg.AddChild(_extractAlgorithmOption);

        var scaleRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        scaleRow.AddThemeConstantOverride("separation", 0);
        outer.AddChild(scaleRow);

        var sizeLabel = MakeCaptionLabel("缩放");
        sizeLabel.CustomMinimumSize = new Vector2(44, 0);
        sizeLabel.TooltipText = "图案在地图画布上的显示大小。";
        scaleRow.AddChild(sizeLabel);

        var stepper = MakeSectionBox(6);
        var stepRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        stepRow.AddThemeConstantOverride("separation", 0);
        stepper.AddChild(stepRow);
        stepRow.AddChild(MakeStepperBtn("\u2212", OnScaleDown));
        _scaleValueLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(52, 0),
            TooltipText = "相对画布的缩放比例。",
        };
        _scaleValueLabel.AddThemeFontSizeOverride("font_size", FontBtnSmall);
        _scaleValueLabel.AddThemeColorOverride("font_color", TextPrimary);
        stepRow.AddChild(_scaleValueLabel);
        stepRow.AddChild(MakeStepperBtn("+", OnScaleUp));
        scaleRow.AddChild(stepper);

        scaleRow.AddChild(MakeSmallGap(10));
        _previewButton = MakeTextBtn("预览", OnPreviewLocal);
        _previewButton.TooltipText = "按当前参数在本地预览线稿效果（仅本机可见，不走联机同步）。";
        scaleRow.AddChild(_previewButton);
    }

    /// <summary>展开后：提取参数、位置微调、重置。</summary>
    private void BuildAdvancedParamsSection(Control parent)
    {
        var row1 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row1.AddThemeConstantOverride("separation", 0);
        parent.AddChild(row1);

        row1.AddChild(MakeParamStepper("精度", out _detailValueLabel, OnDetailDown, OnDetailUp, 52,
            "导入图最长边缩小到此像素以内；越大细节越多，提取越慢。"));
        row1.AddChild(MakeSmallGap(12));
        row1.AddChild(MakeParamStepper("线条", out _maxLinesValueLabel, OnMaxLinesDown, OnMaxLinesUp, 58,
            "最多生成的笔画条数上限。"));

        var row2 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row2.AddThemeConstantOverride("separation", 0);
        parent.AddChild(row2);

        row2.AddChild(MakeParamStepper("边缘", out _edgeValueLabel, OnEdgeDown, OnEdgeUp, 52,
            "二值化灵敏度：数值越大，越容易把弱边算进线稿（笔画通常更多）。"));
        row2.AddChild(MakeSmallGap(12));
        row2.AddChild(MakeParamStepper("最短", out _minLenValueLabel, OnMinLenDown, OnMinLenUp, 40,
            "过滤掉短于此像素数的笔画段。"));

        var row3 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row3.AddThemeConstantOverride("separation", 0);
        parent.AddChild(row3);

        row3.AddChild(MakeParamStepper("断线", out _morphCloseValueLabel, OnMorphCloseDown, OnMorphCloseUp, 40,
            "细化前对二值线稿做形态学闭合 0～2 次：弥合小断口，略增线宽；0 为关闭。骨架模式在细化前执行，Canny 在跟踪前执行。"));

        var row4 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row4.AddThemeConstantOverride("separation", 0);
        parent.AddChild(row4);

        row4.AddChild(MakeParamStepper("衔接", out _joinValueLabel, OnJoinDown, OnJoinUp, 40,
            "排序后若两笔首尾距离在此像素内则并为一笔（参考 auto-painter），减少断笔飞线。0 关闭。"));
        row4.AddChild(MakeSmallGap(12));
        row4.AddChild(MakeParamStepper("对比", out _cannyContrastValueLabel, OnCannyContrastDown, OnCannyContrastUp, 44,
            "仅 Canny：提取前对灰度做线性对比（100%=不变），略大于 100% 可突出弱边。"));

        var row5 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row5.AddThemeConstantOverride("separation", 0);
        parent.AddChild(row5);

        row5.AddChild(MakeParamStepper("速度", out _pointsPerFrameValueLabel, OnPointsPerFrameDown, OnPointsPerFrameUp, 52,
            "自动绘制时每帧推进的顶点数：越大越快；过高可能卡顿，可按机器酌情调高。"));
    }

    private void BuildAdvancedTransformSection(Control parent)
    {
        var sizeRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        sizeRow.AddThemeConstantOverride("separation", 0);
        parent.AddChild(sizeRow);

        var posLabel = MakeCaptionLabel("移动");
        posLabel.CustomMinimumSize = new Vector2(44, 0);
        posLabel.TooltipText = "平移图案在地图上的位置。";
        sizeRow.AddChild(posLabel);
        sizeRow.AddChild(MakeSmallGap(4));

        var arrows = MakeSectionBox(6);
        var arrowRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        arrowRow.AddThemeConstantOverride("separation", 0);
        arrows.AddChild(arrowRow);
        arrowRow.AddChild(MakeStepperBtn("\u2190", () => OnMove(-60f, 0f)));
        arrowRow.AddChild(MakeStepperBtn("\u2193", () => OnMove(0f, 60f)));
        arrowRow.AddChild(MakeStepperBtn("\u2191", () => OnMove(0f, -60f)));
        arrowRow.AddChild(MakeStepperBtn("\u2192", () => OnMove(60f, 0f)));
        sizeRow.AddChild(arrows);

        sizeRow.AddChild(MakeSmallGap(8));
        sizeRow.AddChild(MakeTextBtn("重置", OnResetLayout));
    }

    private Control MakeParamStepper(string label, out Label valueLabel, Action onDown, Action onUp, float valueWidth,
        string? tooltip = null)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 0);

        var caption = MakeCaptionLabel(label);
        caption.CustomMinimumSize = new Vector2(44, 0);
        if (!string.IsNullOrEmpty(tooltip))
        {
            caption.TooltipText = tooltip;
        }

        row.AddChild(caption);

        var box = MakeSectionBox(6);
        if (!string.IsNullOrEmpty(tooltip))
        {
            box.TooltipText = tooltip;
        }

        var inner = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        inner.AddThemeConstantOverride("separation", 0);
        box.AddChild(inner);
        inner.AddChild(MakeStepperBtn("\u2212", onDown));
        valueLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(valueWidth, 0),
        };
        valueLabel.AddThemeFontSizeOverride("font_size", FontBtnSmall);
        valueLabel.AddThemeColorOverride("font_color", TextPrimary);
        if (!string.IsNullOrEmpty(tooltip))
        {
            valueLabel.TooltipText = tooltip;
        }

        inner.AddChild(valueLabel);
        inner.AddChild(MakeStepperBtn("+", onUp));
        row.AddChild(box);

        return row;
    }

    private void BuildFooterSection(Control parent)
    {
        _statusLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", FontCaption);
        _statusLabel.AddThemeColorOverride("font_color", TextSecondary);
        parent.AddChild(_statusLabel);
    }

    private void OnPanelGuiInput(InputEvent inputEvent)
    {
        if (!Visible) return;

        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (mb.Pressed)
            {
                var local = _panel.GetLocalMousePosition();
                var edges = DetectEdges(local);
                if (edges != Edge.None)
                {
                    _isResizing = true;
                    _resizeEdges = edges;
                    _resizeStartMouse = mb.GlobalPosition;
                    _resizeStartWidth = _panel.Size.X;
                    _resizeStartPos = _panel.Position;
                }
                else
                {
                    _isDragging = true;
                    _dragOffset = _panel.Position - mb.GlobalPosition;
                }
            }
            else
            {
                if (_isDragging || _isResizing)
                    SaveWindowLayout();
                _isDragging = false;
                _isResizing = false;
                _resizeEdges = Edge.None;
            }
            GetViewport().SetInputAsHandled();
            _panel.AcceptEvent();
            return;
        }

        if (inputEvent is InputEventMouseMotion mm)
        {
            if (_isResizing)
            {
                var delta = mm.GlobalPosition - _resizeStartMouse;
                var newWidth = _resizeStartWidth;
                var newPos = _resizeStartPos;

                if ((_resizeEdges & Edge.Right) != 0)
                    newWidth = _resizeStartWidth + delta.X;
                if ((_resizeEdges & Edge.Left) != 0)
                {
                    newWidth = _resizeStartWidth - delta.X;
                    newPos.X = _resizeStartPos.X + delta.X;
                }

                newWidth = Math.Clamp(newWidth, MinW, MaxW);

                if ((_resizeEdges & Edge.Left) != 0)
                    newPos.X = _resizeStartPos.X + (_resizeStartWidth - newWidth);

                _panel.CustomMinimumSize = new Vector2(newWidth, 0);
                _panel.Size = new Vector2(newWidth, 0);
                _panel.Position = newPos;
            }
            else if (_isDragging)
            {
                _panel.Position = mm.GlobalPosition + _dragOffset;
            }
            else
            {
                var local = _panel.GetLocalMousePosition();
                var edges = DetectEdges(local);
                _panel.MouseDefaultCursorShape = edges switch
                {
                    Edge.Left or Edge.Right => Control.CursorShape.Hsize,
                    _ => Control.CursorShape.Arrow,
                };
            }
            GetViewport().SetInputAsHandled();
            _panel.AcceptEvent();
        }
    }

    private Edge DetectEdges(Vector2 localPos)
    {
        var size = _panel.Size;
        var edges = Edge.None;
        if (localPos.X < GrabZone)
            edges |= Edge.Left;
        else if (localPos.X > size.X - GrabZone)
            edges |= Edge.Right;
        return edges;
    }

    private void SaveWindowLayout()
    {
        var s = MapPaintSettings.Load();
        s.WindowX = _panel.Position.X;
        s.WindowY = _panel.Position.Y;
        s.WindowWidth = _panel.Size.X;
        s.Save();
    }

    private void OnPrevImage()
    {
        var settings = MapPaintSettings.Load();
        var current = MapImportLibrary.ResolveSelectedImage(settings);
        var prev = MapImportLibrary.CycleSelection(current, -1);
        if (prev is not null)
            ApplyImagePath(prev);
        else
            Refresh("图库为空，请将图片放入文件夹。");
    }

    private void OnNextImage()
    {
        var settings = MapPaintSettings.Load();
        var current = MapImportLibrary.ResolveSelectedImage(settings);
        var next = MapImportLibrary.CycleSelection(current, 1);
        if (next is not null)
            ApplyImagePath(next);
        else
            Refresh("图库为空，请将图片放入文件夹。");
    }

    private void OnOpenFolder()
    {
        var folder = MapImportLibrary.GetImportFolder();

        if (MapImportLibrary.IsMobile)
        {
            bool opened = false;
            try
            {
                var err = OS.ShellShowInFileManager(folder + "/");
                if (err == Error.Ok)
                    opened = true;
            }
            catch { }

            try { DisplayServer.ClipboardSet(folder); } catch { }

            Refresh(opened
                ? $"已尝试打开文件夹，添加图片后请点刷新。\n路径已复制到剪贴板。"
                : $"路径已复制到剪贴板，请用文件管理器导航到:\n{folder}");
            return;
        }

        try
        {
            var err = OS.ShellShowInFileManager(folder.Replace('/', '\\'));
            if (err != Error.Ok)
                OS.ShellOpen(folder);
            Refresh("已尝试打开文件夹，添加图片后请点击刷新。");
        }
        catch (Exception ex)
        {
            ModLog.Info($"MapImportPanel: failed to open folder: {ex.Message}");
            Refresh("无法打开文件夹。");
        }
    }

    private void ImportPickedFile(string path)
    {
        Refresh($"已选中，正在导入…\n{path}");

        try
        {
            var folder = MapImportLibrary.GetImportFolder();
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var fileName = SanitizeFileName(path);
            var dest = Path.Combine(folder, fileName);

            var image = new Image();
            var loadErr = image.Load(path);

            if (loadErr == Error.Ok && image.GetWidth() > 0)
            {
                var saveErr = image.SavePng(dest);
                if (saveErr != Error.Ok)
                {
                    Refresh($"[导入失败] SavePng: {saveErr}\n目标: {dest}");
                    return;
                }
            }
            else
            {
                var copyErr = TryCopyViaFileAccess(path, dest);
                if (copyErr != Error.Ok)
                {
                    Refresh($"[导入失败] Image.Load={loadErr}, FileAccess={copyErr}\n路径: {path}");
                    return;
                }
            }

            if (!System.IO.File.Exists(dest) || new System.IO.FileInfo(dest).Length == 0)
            {
                Refresh($"[导入失败] 写入后文件为空\n目标: {dest}");
                return;
            }

            MapImportLibrary.RefreshCache();
            var s = MapPaintSettings.Load();
            s.ImagePath = dest;
            s.Save();
            Refresh($"已导入: {fileName} ({image.GetWidth()}x{image.GetHeight()})");
        }
        catch (Exception ex)
        {
            Refresh($"[导入异常] {ex.GetType().Name}: {ex.Message}\n路径: {path}");
        }
    }

    private static Error TryCopyViaFileAccess(string srcPath, string destPath)
    {
        using var src = Godot.FileAccess.Open(srcPath, Godot.FileAccess.ModeFlags.Read);
        if (src is null)
            return Godot.FileAccess.GetOpenError();

        var data = src.GetBuffer((long)src.GetLength());
        src.Close();
        if (data.Length == 0)
            return Error.Failed;

        System.IO.File.WriteAllBytes(destPath, data);
        return Error.Ok;
    }

    private static string SanitizeFileName(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && !path.Contains("://"))
        {
            var name = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        var ext = ".png";
        var lower = path.ToLowerInvariant();
        if (lower.Contains(".jpg") || lower.Contains(".jpeg")) ext = ".jpg";
        else if (lower.Contains(".webp")) ext = ".webp";
        else if (lower.Contains(".bmp")) ext = ".bmp";
        return $"imported_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
    }

    private void OnOpenUploadPage()
    {
        if (_server is null || !_server.IsRunning)
        {
            StartUploadServer();
        }

        if (_server is not null && _server.IsRunning)
        {
            try
            {
                OS.ShellOpen(_server.Url);
                Refresh($"已在浏览器中打开上传页面\n地址: {_server.Url}");
            }
            catch
            {
                Refresh($"请手动在浏览器中打开:\n{_server.Url}");
            }
        }
        else
        {
            Refresh("上传服务启动失败，请检查日志。");
        }
    }

    private void OnRefreshImages()
    {
        MapImportLibrary.RefreshCache();
        var images = MapImportLibrary.GetAvailableImages();
        Refresh($"已刷新，共找到 {images.Length} 张图片。");
    }

    private void OnDeleteSelectedImage()
    {
        var settings = MapPaintSettings.Load();
        var path = MapImportLibrary.ResolveSelectedImage(settings);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Refresh("当前没有选中的图片可删除。");
            return;
        }

        MapStrokePlaybackProgress.DeleteFile();

        var name = Path.GetFileName(path);
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Refresh($"删除失败: {ex.Message}");
            return;
        }

        MapImportLibrary.RefreshCache();
        var images = MapImportLibrary.GetAvailableImages();
        if (images.Length > 0)
        {
            settings.ImagePath = images[0];
        }
        else
        {
            settings.ImagePath = string.Empty;
        }

        settings.Save();
        Refresh($"已删除: {name}");
        AutoRedraw();
    }

    private void ApplyImagePath(string path)
    {
        MapStrokePlaybackProgress.DeleteFile();
        var s = MapPaintSettings.Load();
        s.ImagePath = path;
        s.Save();
        MapImportLibrary.RefreshCache();
        Refresh($"已选择: {Path.GetFileName(path)}");
    }

    private void OnPreviewLocal()
    {
        if (MapStrokeInputPlayback.IsActive)
        {
            Refresh("请先停止绘制再预览。");
            return;
        }

        var r = MapAutoPainter.TryApply(NMapScreen.Instance);
        Refresh(r.Message);
    }

    private void OnDrawAction()
    {
        if (MapStrokeInputPlayback.IsActive)
        {
            var r = MapStrokeInputPlayback.TryStop(saveProgress: true);
            Refresh(r.Message);
        }
        else
        {
            var r = MapStrokeInputPlayback.TryStart(NMapScreen.Instance);
            Refresh(r.Message);
        }
    }

    private void OnDetailUp() => AdjustDetail(128);
    private void OnDetailDown() => AdjustDetail(-128);

    private void AdjustDetail(int delta)
    {
        var s = MapPaintSettings.Load();
        s.MaxImageSize = Math.Clamp(s.MaxImageSize + delta, 256, 2048);
        s.Save();
        Refresh($"精度: {s.MaxImageSize}px");
        AutoRedraw();
    }

    private void OnMaxLinesUp() => AdjustMaxLines(2000);
    private void OnMaxLinesDown() => AdjustMaxLines(-2000);

    private void OnPointsPerFrameUp() => AdjustPointsPerFrame(8);
    private void OnPointsPerFrameDown() => AdjustPointsPerFrame(-8);

    private void AdjustPointsPerFrame(int delta)
    {
        var s = MapPaintSettings.Load();
        s.PointsPerFrame = Math.Clamp(s.PointsPerFrame + delta, 8, 512);
        s.Save();
        Refresh($"绘制速度: {s.PointsPerFrame} 点/帧");
    }

    private void AdjustMaxLines(int delta)
    {
        var s = MapPaintSettings.Load();
        s.MaxStrokes = Math.Clamp(s.MaxStrokes + delta, 1000, 30000);
        s.Save();
        Refresh($"最大线条数: {s.MaxStrokes}");
        AutoRedraw();
    }

    private void OnExtractAlgorithmSelected(long index)
    {
        if (_syncingExtractAlgorithm)
        {
            return;
        }

        var s = MapPaintSettings.Load();
        s.ExtractAlgorithm = index == 0 ? "canny" : "skeleton";
        s.Save();
        Refresh(index == 0 ? "已切换为 Canny 提取。" : "已切换为骨架 XDoG 提取。");
        AutoRedraw();
    }

    private void OnEdgeUp() => AdjustEdge(0.05f);
    private void OnEdgeDown() => AdjustEdge(-0.05f);

    private void AdjustEdge(float delta)
    {
        var s = MapPaintSettings.Load();
        s.DarkThreshold = MathF.Round(Math.Clamp(s.DarkThreshold + delta, 0.1f, 0.95f), 2);
        s.Save();
        Refresh($"边缘阈值: {s.DarkThreshold:F2}");
        AutoRedraw();
    }

    private void OnMinLenUp() => AdjustMinLen(1);
    private void OnMinLenDown() => AdjustMinLen(-1);

    private void OnMorphCloseUp() => AdjustMorphClose(1);
    private void OnMorphCloseDown() => AdjustMorphClose(-1);

    private void AdjustMorphClose(int delta)
    {
        var s = MapPaintSettings.Load();
        s.MorphCloseIterations = Math.Clamp(s.MorphCloseIterations + delta, 0, 2);
        s.Save();
        Refresh($"断线补全: {s.MorphCloseIterations}（0=关）");
        AutoRedraw();
    }

    private void OnJoinUp() => AdjustJoin(1);
    private void OnJoinDown() => AdjustJoin(-1);

    private void AdjustJoin(int delta)
    {
        var s = MapPaintSettings.Load();
        s.StrokeJoinPixels = Math.Clamp(s.StrokeJoinPixels + delta, 0, 20);
        s.Save();
        Refresh($"笔画衔接: {s.StrokeJoinPixels}px（0=不合并）");
        AutoRedraw();
    }

    private void OnCannyContrastUp() => AdjustCannyContrast(0.05f);
    private void OnCannyContrastDown() => AdjustCannyContrast(-0.05f);

    private void AdjustCannyContrast(float delta)
    {
        var s = MapPaintSettings.Load();
        s.CannyContrast = MathF.Round(Math.Clamp(s.CannyContrast + delta, 0.5f, 1.5f), 2);
        s.Save();
        Refresh($"Canny 对比: {(int)Math.Round(s.CannyContrast * 100f)}%");
        AutoRedraw();
    }

    private void AdjustMinLen(int delta)
    {
        var s = MapPaintSettings.Load();
        s.MinStrokeLength = Math.Clamp(s.MinStrokeLength + delta, 1, 30);
        s.Save();
        Refresh($"最短笔画: {s.MinStrokeLength}");
        AutoRedraw();
    }

    private void OnScaleUp() => AdjustScale(0.1f);
    private void OnScaleDown() => AdjustScale(-0.1f);

    private void AdjustScale(float delta)
    {
        var s = MapPaintSettings.Load();
        s.DrawScale = Math.Clamp(s.DrawScale + delta, 0.1f, 5f);
        s.Save();
        Refresh($"缩放: {(int)(s.DrawScale * 100)}%");
        AutoRedraw();
    }

    private void OnMove(float dx, float dy)
    {
        var s = MapPaintSettings.Load();
        s.OffsetX += dx;
        s.OffsetY += dy;
        s.Save();
        Refresh($"偏移: ({(int)s.OffsetX}, {(int)s.OffsetY})");
        AutoRedraw();
    }

    private void OnResetLayout()
    {
        var s = MapPaintSettings.Load();
        s.DrawScale = 0.8f;
        s.OffsetX = 0f;
        s.OffsetY = 0f;
        s.MaxImageSize = 1536;
        s.MaxStrokes = 5000;
        s.DarkThreshold = 0.80f;
        s.MinStrokeLength = 2;
        s.MorphCloseIterations = 1;
        s.StrokeJoinPixels = 3;
        s.CannyContrast = 1.30f;
        s.PointsPerFrame = 512;
        s.ExtractAlgorithm = "canny";
        s.WindowX = 24f;
        s.WindowY = 72f;
        s.WindowWidth = 450f;
        s.Save();
        _panel.Position = new Vector2(24f, 72f);
        _panel.CustomMinimumSize = new Vector2(450f, 0f);
        Refresh("已全部重置。");
        AutoRedraw();
    }

    private void AutoRedraw()
    {
    }

    private static Control MakeSmallGap(float width)
    {
        return new Control
        {
            CustomMinimumSize = new Vector2(width, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
    }

    private static Label MakeCaptionLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", FontCaption);
        label.AddThemeColorOverride("font_color", TextSecondary);
        return label;
    }

    private static ColorRect MakeDivider()
    {
        return new ColorRect
        {
            Color = SeparatorColor,
            CustomMinimumSize = new Vector2(0, 1),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
    }

    private static PanelContainer MakeSectionBox(int radius = RadiusSection)
    {
        var box = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeStyleboxOverride("panel", MakeStyle(BgSection, default, 0, radius, 8, 6));
        return box;
    }

    private static PanelBtn MakeIconBtn(string icon, Action onPressed)
    {
        return new PanelBtn(icon, onPressed, PanelBtn.Kind.Icon);
    }

    private static PanelBtn MakePillBtn(string text, Action onPressed, bool primary = false)
    {
        return new PanelBtn(text, onPressed, primary ? PanelBtn.Kind.Primary : PanelBtn.Kind.Pill);
    }

    private static PanelBtn MakeGreenPillBtn(string text, Action onPressed) =>
        new PanelBtn(text, onPressed, PanelBtn.Kind.GreenPill);

    private static PanelBtn MakeRedPillBtn(string text, Action onPressed) =>
        new PanelBtn(text, onPressed, PanelBtn.Kind.RedPill);

    private static PanelBtn MakeStepperBtn(string text, Action onPressed)
    {
        return new PanelBtn(text, onPressed, PanelBtn.Kind.Stepper);
    }

    private static PanelBtn MakeTextBtn(string text, Action onPressed)
    {
        return new PanelBtn(text, onPressed, PanelBtn.Kind.Text);
    }

    private static StyleBoxFlat MakeStyle(Color bg, Color border, int borderWidth,
        int radius, int padH = 0, int padV = 0)
    {
        return new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthBottom = borderWidth,
            BorderWidthLeft = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthTop = borderWidth,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            ContentMarginLeft = padH,
            ContentMarginRight = padH,
            ContentMarginTop = padV,
            ContentMarginBottom = padV,
        };
    }
}

internal sealed partial class PanelBtn : Button
{
    internal enum Kind { Pill, Primary, Icon, Stepper, Text, GreenPill, RedPill }

    private static readonly Color BgNormal = new(1f, 1f, 1f, 0.16f);
    private static readonly Color BgHover = new(1f, 1f, 1f, 0.24f);
    private static readonly Color BgPress = new(1f, 1f, 1f, 0.10f);
    private static readonly Color BgOff = new(1f, 1f, 1f, 0.05f);
    private static readonly Color Blue = new(0.04f, 0.52f, 1f, 1f);
    private static readonly Color BlueHover = new(0.3f, 0.63f, 1f, 1f);
    private static readonly Color BluePress = new(0.04f, 0.42f, 0.85f, 1f);
    private static readonly Color Green = new(0.13f, 0.65f, 0.35f, 1f);
    private static readonly Color GreenHover = new(0.22f, 0.75f, 0.45f, 1f);
    private static readonly Color GreenPress = new(0.10f, 0.52f, 0.28f, 1f);
    private static readonly Color Red = new(0.86f, 0.22f, 0.22f, 1f);
    private static readonly Color RedHover = new(0.95f, 0.38f, 0.38f, 1f);
    private static readonly Color RedPress = new(0.72f, 0.16f, 0.16f, 1f);
    private static readonly Color TxtPrimary = new(1f, 1f, 1f, 0.95f);
    private static readonly Color TxtSecondary = new(0.92f, 0.92f, 0.96f, 0.70f);
    private static readonly Color TxtDisabled = new(1f, 1f, 1f, 0.25f);

    private readonly Action _action;
    private bool _off;

    public PanelBtn(string text, Action action, Kind kind)
    {
        _action = action;
        ProcessMode = ProcessModeEnum.Always;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        FocusMode = FocusModeEnum.None;
        Text = text;
        ClipText = false;

        int radius, fontSize, padH, padV;
        Color fg, fgHover, bgN, bgH, bgP, bgD;
        bool mobile = MapImportLibrary.IsMobile;

        switch (kind)
        {
            case Kind.Primary:
                radius = 14;
                fontSize = mobile ? 17 : 14;
                padH = mobile ? 24 : 20;
                padV = mobile ? 13 : 8;
                bgN = Blue;
                bgH = BlueHover;
                bgP = BluePress;
                bgD = BgOff;
                fg = Colors.White;
                fgHover = Colors.White;
                break;
            case Kind.Icon:
                radius = 8;
                fontSize = mobile ? 16 : 13;
                padH = mobile ? 12 : 8;
                padV = mobile ? 10 : 6;
                bgN = BgNormal;
                bgH = BgHover;
                bgP = BgPress;
                bgD = BgOff;
                fg = TxtSecondary;
                fgHover = TxtPrimary;
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
                break;
            case Kind.Stepper:
                radius = 6;
                fontSize = mobile ? 18 : 14;
                padH = mobile ? 18 : 12;
                padV = mobile ? 10 : 5;
                bgN = new Color(1f, 1f, 1f, 0.10f);
                bgH = BgHover;
                bgP = BgPress;
                bgD = BgOff;
                fg = TxtSecondary;
                fgHover = TxtPrimary;
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
                break;
            case Kind.Text:
                radius = 6;
                fontSize = mobile ? 16 : 13;
                padH = mobile ? 16 : 10;
                padV = mobile ? 10 : 5;
                bgN = new Color(1f, 1f, 1f, 0.08f);
                bgH = BgHover;
                bgP = BgPress;
                bgD = new Color(1f, 1f, 1f, 0.04f);
                fg = Blue;
                fgHover = BlueHover;
                SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
                break;
            case Kind.GreenPill:
            case Kind.RedPill:
                radius = 14;
                fontSize = mobile ? 17 : 14;
                padH = mobile ? 22 : 16;
                padV = mobile ? 13 : 8;
                if (kind == Kind.GreenPill)
                {
                    bgN = Green;
                    bgH = GreenHover;
                    bgP = GreenPress;
                }
                else
                {
                    bgN = Red;
                    bgH = RedHover;
                    bgP = RedPress;
                }
                bgD = BgOff;
                fg = Colors.White;
                fgHover = Colors.White;
                break;
            default:
                radius = 14;
                fontSize = mobile ? 17 : 14;
                padH = mobile ? 22 : 16;
                padV = mobile ? 13 : 8;
                bgN = BgNormal;
                bgH = BgHover;
                bgP = BgPress;
                bgD = BgOff;
                fg = TxtPrimary;
                fgHover = TxtPrimary;
                break;
        }

        AddThemeStyleboxOverride("normal", S(bgN, radius, padH, padV));
        AddThemeStyleboxOverride("hover", S(bgH, radius, padH, padV));
        AddThemeStyleboxOverride("pressed", S(bgP, radius, padH, padV));
        AddThemeStyleboxOverride("hover_pressed", S(bgP, radius, padH, padV));
        AddThemeStyleboxOverride("disabled", S(bgD, radius, padH, padV));
        AddThemeStyleboxOverride("focus", S(new Color(0, 0, 0, 0), radius, padH, padV));
        AddThemeFontSizeOverride("font_size", fontSize);
        AddThemeColorOverride("font_color", fg);
        AddThemeColorOverride("font_hover_color", fgHover);
        AddThemeColorOverride("font_pressed_color", fgHover);
        AddThemeColorOverride("font_disabled_color", TxtDisabled);
        AddThemeConstantOverride("h_separation", 0);
        AddThemeConstantOverride("outline_size", 0);

        Pressed += OnBtnPressed;
    }

    public new void SetDisabled(bool disabled)
    {
        _off = disabled;
        Disabled = disabled;
        MouseFilter = disabled ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop;
    }

    private void OnBtnPressed()
    {
        if (_off) return;
        _action();
        GetViewport().SetInputAsHandled();
        AcceptEvent();
    }

    private static StyleBoxFlat S(Color bg, int r, int ph, int pv)
    {
        return new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusBottomLeft = r,
            CornerRadiusBottomRight = r,
            CornerRadiusTopLeft = r,
            CornerRadiusTopRight = r,
            ContentMarginLeft = ph,
            ContentMarginRight = ph,
            ContentMarginTop = pv,
            ContentMarginBottom = pv,
        };
    }
}
