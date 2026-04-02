using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using AncientPredictor.FuturePredictor;

namespace AncientPredictor.UI;

/// <summary>
/// A floating overlay panel that displays Ancient predictions on the map screen.
/// - Only visible when the map screen is open.
/// - Can be collapsed/expanded by clicking the header.
/// - Draggable by the header bar.
/// - Auto-refreshes predictions when the map opens.
/// </summary>
public partial class AncientOverlay : Control
{
    // ── State ──────────────────────────────────────────────
    private bool _isCollapsed;
    private bool _isDragging;
    private Vector2 _dragOffset;
    private bool _predictionsStale = true;

    // ── Theme constants ───────────────────────────────────
    private static readonly Color HeaderBg       = new(0.35f, 0.13f, 0.53f, 0.95f);   // dark purple
    private static readonly Color HeaderBgHover  = new(0.45f, 0.20f, 0.63f, 0.95f);
    private static readonly Color PanelBg        = new(0.10f, 0.08f, 0.14f, 0.92f);   // dark navy
    private static readonly Color AccentPurple   = new(0.60f, 0.40f, 0.90f, 1.0f);
    private static readonly Color TextWhite      = new(0.95f, 0.95f, 0.95f, 1.0f);
    private static readonly Color TextGold       = new(1.0f, 0.84f, 0.0f, 1.0f);
    private static readonly Color TextGray       = new(0.65f, 0.65f, 0.70f, 1.0f);
    private static readonly Color OptionBorder   = new(0.55f, 0.35f, 0.80f, 0.6f);
    private static readonly Color SeparatorColor = new(0.40f, 0.30f, 0.55f, 0.4f);

    private const float PanelWidth    = 360f;
    private const float HeaderHeight  = 36f;
    private const float PanelPadding  = 12f;
    private const float OptionSpacing = 8f;

    // ── Child nodes (built programmatically) ──────────────
    private Panel _headerPanel = null!;
    private Label _headerLabel = null!;
    private Label _toggleIcon  = null!;
    private VBoxContainer _contentContainer = null!;
    private Panel _bgPanel = null!;

    // ── Singleton ─────────────────────────────────────────
    public static AncientOverlay? Instance { get; private set; }

    public override void _Ready()
    {
        Instance = this;
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 100;
        BuildUi();
        Visible = false;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    // ------------------------------------------------------------------
    // UI construction
    // ------------------------------------------------------------------
    private void BuildUi()
    {
        // Root anchor: top-right corner
        Size = new Vector2(PanelWidth, HeaderHeight);
        Position = new Vector2(1920f - PanelWidth - 20f, 80f);  // default, will be clamped

        // ── Background panel ────────────────────────────
        _bgPanel = new Panel();
        _bgPanel.MouseFilter = MouseFilterEnum.Ignore;
        var bgStyle = new StyleBoxFlat
        {
            BgColor = PanelBg,
            CornerRadiusBottomLeft  = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft  = 0,
            ContentMarginRight = 0,
            ContentMarginTop   = 0,
            ContentMarginBottom = 0,
        };
        _bgPanel.AddThemeStyleboxOverride("panel", bgStyle);
        _bgPanel.Position = new Vector2(0, HeaderHeight);
        _bgPanel.Size = new Vector2(PanelWidth, 0);
        _bgPanel.Visible = false;
        AddChild(_bgPanel);

        // ── Header bar ─────────────────────────────────
        _headerPanel = new Panel();
        _headerPanel.MouseFilter = MouseFilterEnum.Stop;
        // Initially expanded state: bottom corners = 0 (panel sits below header)
        var headerStyle = new StyleBoxFlat
        {
            BgColor = HeaderBg,
            CornerRadiusTopLeft     = 8,
            CornerRadiusTopRight    = 8,
            CornerRadiusBottomLeft  = 0,
            CornerRadiusBottomRight = 0,
        };
        _headerPanel.AddThemeStyleboxOverride("panel", headerStyle);
        _headerPanel.Size = new Vector2(PanelWidth, HeaderHeight);
        _headerPanel.Position = Vector2.Zero;
        AddChild(_headerPanel);

        _headerLabel = new Label();
        _headerLabel.Text = "Ancient Predictor";
        _headerLabel.Position = new Vector2(10, 6);
        _headerLabel.AddThemeColorOverride("font_color", TextWhite);
        _headerLabel.AddThemeFontSizeOverride("font_size", 16);
        _headerPanel.AddChild(_headerLabel);

        _toggleIcon = new Label();
        _toggleIcon.Text = "\u25bc"; // ▼ down arrow
        _toggleIcon.Position = new Vector2(PanelWidth - 28, 6);
        _toggleIcon.AddThemeColorOverride("font_color", TextWhite);
        _toggleIcon.AddThemeFontSizeOverride("font_size", 16);
        _headerPanel.AddChild(_toggleIcon);

        // ── Content container (inside bg panel) ────────
        _contentContainer = new VBoxContainer();
        _contentContainer.Position = new Vector2(PanelPadding, PanelPadding);
        _contentContainer.Size = new Vector2(PanelWidth - PanelPadding * 2, 0);
        _contentContainer.AddThemeConstantOverride("separation", (int)OptionSpacing);
        _bgPanel.AddChild(_contentContainer);

        // Connect header input
        _headerPanel.GuiInput += OnHeaderInput;
        _headerPanel.MouseEntered += OnHeaderMouseEntered;
        _headerPanel.MouseExited += OnHeaderMouseExited;
    }

    // ------------------------------------------------------------------
    // Input handling
    // ------------------------------------------------------------------
    private void OnHeaderInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _isDragging = true;
                    _dragOffset = GetGlobalMousePosition() - GlobalPosition;
                }
                else
                {
                    if (_isDragging)
                    {
                        var moved = (GetGlobalMousePosition() - GlobalPosition - _dragOffset).Length();
                        if (moved < 5f)
                        {
                            // Click: toggle collapsed
                            ToggleCollapsed();
                        }
                    }
                    _isDragging = false;
                }
            }
        }
        else if (ev is InputEventMouseMotion && _isDragging)
        {
            Position = GetGlobalMousePosition() - _dragOffset;
            ClampPosition();
        }
    }

    private void OnHeaderMouseEntered()
    {
        var style = (_headerPanel.GetThemeStylebox("panel") as StyleBoxFlat)!;
        style.BgColor = HeaderBgHover;
    }

    private void OnHeaderMouseExited()
    {
        var style = (_headerPanel.GetThemeStylebox("panel") as StyleBoxFlat)!;
        style.BgColor = HeaderBg;
    }

    private void ClampPosition()
    {
        var vp = GetViewportRect().Size;
        var x = Mathf.Clamp(Position.X, 0, vp.X - PanelWidth);
        var y = Mathf.Clamp(Position.Y, 0, vp.Y - HeaderHeight);
        Position = new Vector2(x, y);
    }

    // ------------------------------------------------------------------
    // Collapse / Expand
    // ------------------------------------------------------------------
    private void ToggleCollapsed()
    {
        _isCollapsed = !_isCollapsed;
        _bgPanel.Visible = !_isCollapsed;
        _toggleIcon.Text = _isCollapsed ? "\u25b6" : "\u25bc"; // ▶ or ▼

        // Round bottom corners of header when collapsed
        var style = (_headerPanel.GetThemeStylebox("panel") as StyleBoxFlat)!;
        style.CornerRadiusBottomLeft  = _isCollapsed ? 8 : 0;
        style.CornerRadiusBottomRight = _isCollapsed ? 8 : 0;

        if (!_isCollapsed && _predictionsStale)
        {
            RefreshPredictions();
        }
    }

    // ------------------------------------------------------------------
    // Show / Hide (called from map screen hooks)
    // ------------------------------------------------------------------
    public void ShowOnMap()
    {
        Visible = true;
        if (_predictionsStale)
            RefreshPredictions();
    }

    public void HideFromMap()
    {
        Visible = false;
    }

    /// <summary>
    /// Mark predictions as stale (e.g., on new run, save load, act change).
    /// </summary>
    public void MarkStale()
    {
        _predictionsStale = true;
        FuturePredictor.AncientPredictor.ClearCache();
    }

    // ------------------------------------------------------------------
    // Prediction rendering
    // ------------------------------------------------------------------
    public void RefreshPredictions()
    {
        _predictionsStale = false;

        // Clear old content
        foreach (var child in _contentContainer.GetChildren())
        {
            child.QueueFree();
        }

        // Get player
        Player? player = GetCurrentPlayer();
        if (player == null)
        {
            AddInfoLabel("Waiting for run data...");
            ResizePanel();
            return;
        }

        try
        {
            var acts = FuturePredictor.AncientPredictor.PredictAllAncients(player);
            if (acts.Count == 0)
            {
                AddInfoLabel("No Ancient data available.");
                ResizePanel();
                return;
            }

            foreach (var act in acts)
            {
                AddActSection(act);
            }
        }
        catch (Exception ex)
        {
            AddInfoLabel($"Error: {ex.Message}");
        }

        ResizePanel();
    }

    private void AddActSection(FuturePredictor.AncientPredictor.AncientActResult act)
    {
        // ── Act header ──
        var actHeader = new Label();
        actHeader.Text = $"Act {act.ActIndex + 1} - {act.AncientType}";
        actHeader.AddThemeColorOverride("font_color", TextGold);
        actHeader.AddThemeFontSizeOverride("font_size", 15);
        _contentContainer.AddChild(actHeader);

        // ── Separator line ──
        var sep = new HSeparator();
        sep.AddThemeStyleboxOverride("separator", new StyleBoxFlat
        {
            BgColor = SeparatorColor,
            ContentMarginTop = 1,
            ContentMarginBottom = 1,
        });
        _contentContainer.AddChild(sep);

        // ── Options ──
        for (int i = 0; i < act.Options.Count; i++)
        {
            var opt = act.Options[i];
            AddOptionCard(i + 1, opt);
        }

        // Spacer between acts
        var spacer = new Control();
        spacer.CustomMinimumSize = new Vector2(0, 6);
        _contentContainer.AddChild(spacer);
    }

    private void AddOptionCard(int index, FuturePredictor.AncientPredictor.AncientOptionResult opt)
    {
        var card = new PanelContainer();
        var cardStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.18f, 0.14f, 0.24f, 0.8f),
            BorderColor = OptionBorder,
            BorderWidthLeft   = 3,
            BorderWidthRight  = 0,
            BorderWidthTop    = 0,
            BorderWidthBottom = 0,
            CornerRadiusTopLeft     = 4,
            CornerRadiusBottomLeft  = 4,
            CornerRadiusTopRight    = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft   = 10,
            ContentMarginRight  = 8,
            ContentMarginTop    = 6,
            ContentMarginBottom = 6,
        };
        card.AddThemeStyleboxOverride("panel", cardStyle);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 2);

        // Title line: "1. Display Name"
        var titleLabel = new Label();
        titleLabel.Text = $"{index}. {opt.DisplayName}";
        titleLabel.AddThemeColorOverride("font_color", TextWhite);
        titleLabel.AddThemeFontSizeOverride("font_size", 14);
        titleLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(titleLabel);

        // Subtitle: internal relic id
        if (opt.RelicId != opt.DisplayName)
        {
            var idLabel = new Label();
            idLabel.Text = opt.RelicId;
            idLabel.AddThemeColorOverride("font_color", TextGray);
            idLabel.AddThemeFontSizeOverride("font_size", 11);
            vbox.AddChild(idLabel);
        }

        // Note (if any)
        if (!string.IsNullOrEmpty(opt.Note))
        {
            var noteLabel = new Label();
            noteLabel.Text = opt.Note;
            noteLabel.AddThemeColorOverride("font_color", AccentPurple);
            noteLabel.AddThemeFontSizeOverride("font_size", 11);
            noteLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            vbox.AddChild(noteLabel);
        }

        card.AddChild(vbox);
        _contentContainer.AddChild(card);
    }

    private void AddInfoLabel(string text)
    {
        var lbl = new Label();
        lbl.Text = text;
        lbl.AddThemeColorOverride("font_color", TextGray);
        lbl.AddThemeFontSizeOverride("font_size", 13);
        lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _contentContainer.AddChild(lbl);
    }

    private void ResizePanel()
    {
        // Let Godot compute child sizes first, then adjust
        CallDeferred(nameof(DeferredResize));
    }

    private void DeferredResize()
    {
        if (_isCollapsed)
        {
            _bgPanel.Visible = false;
            return;
        }

        // Calculate total content height
        float totalH = PanelPadding * 2;
        foreach (var child in _contentContainer.GetChildren())
        {
            if (child is Control c)
            {
                totalH += c.Size.Y + OptionSpacing;
            }
        }
        totalH = Mathf.Max(totalH, 60f);
        totalH = Mathf.Min(totalH, 600f);

        _bgPanel.Size = new Vector2(PanelWidth, totalH);
        _contentContainer.Size = new Vector2(PanelWidth - PanelPadding * 2, totalH - PanelPadding * 2);
        _bgPanel.Visible = true;
    }

    // ------------------------------------------------------------------
    // Player access (same approach as mcp-mod)
    // ------------------------------------------------------------------
    private static Player? GetCurrentPlayer()
    {
        try
        {
            var instance = RunManager.Instance;
            var state = instance.DebugOnlyGetState();
            if (state == null) return null;
            return LocalContext.GetMe(state);
        }
        catch
        {
            return null;
        }
    }
}
