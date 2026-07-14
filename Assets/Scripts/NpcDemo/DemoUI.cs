using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shared on-screen-UI settings for the demo: a window-relative scale (so menus, HUD,
/// tips and control text grow with the window but never shrink below their readable base
/// size) plus the collapse state of the HUD panels. Each panel draws a title bar with a
/// minimize/expand button (like window chrome, minus close) via PanelHeader.
///
/// OnGUI code multiplies GUI.matrix by Scale and works in "virtual" pixels
/// (Screen.width/Scale x Screen.height/Scale), so a single factor scales fonts and layout
/// together.
/// </summary>
public static class DemoUI
{
    /// <summary>World-space NPC state labels (kept always-on; panels collapse instead).</summary>
    public static bool ShowHud = true;
    /// <summary>World-space survivor tip bubbles (kept always-on; panels collapse instead).</summary>
    public static bool ShowTips = true;

    private const float ReferenceHeight = 900f;

    /// <summary>1.0 at/below the reference height, growing up to 2.5x on large windows.</summary>
    public static float Scale
    {
        get { return Mathf.Clamp(Screen.height / ReferenceHeight, 1f, 2.5f); }
    }

    // ------------------------------------------------ collapsible panels ("windows")

    /// <summary>Height of a collapsed panel: just its title bar.</summary>
    public const float PanelBarHeight = 34f;

    private static readonly Dictionary<string, bool> collapsedPanels = new Dictionary<string, bool>();
    private static GUIStyle headerStyle;
    private static GUIStyle headerButtonStyle;

    public static bool IsCollapsed(string id)
    {
        bool collapsed;
        return collapsedPanels.TryGetValue(id, out collapsed) && collapsed;
    }

    /// <summary>
    /// Draws a panel title bar with a minimize ("-") / expand ("+") button on the right.
    /// Call inside a GUILayout.BeginArea. Returns true when the panel body should draw.
    /// </summary>
    public static bool PanelHeader(string id, string title)
    {
        if (headerStyle == null)
        {
            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 15
            };
            headerButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 14
            };
        }

        bool collapsed = IsCollapsed(id);
        GUILayout.BeginHorizontal();
        GUILayout.Label(title, headerStyle);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(collapsed ? "+" : "-", headerButtonStyle, GUILayout.Width(26), GUILayout.Height(20)))
        {
            collapsedPanels[id] = !collapsed;
        }
        GUILayout.EndHorizontal();
        return !IsCollapsed(id);
    }
}
