using System;
using System.Collections.Generic;
using Nitrox.Model.ModSync;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NitroxClient.MonoBehaviours.Gui.MainMenu.ModSync;

/// <summary>
///     Main menu panel used while joining a server to explain mod differences, ask for consent, and show download progress.
///     Built from clones of Subnautica's own main menu elements (header text, "New Game" button) so it matches the game's style.
/// </summary>
public class MainMenuModSyncPanel : MonoBehaviour, uGUI_INavigableIconGrid, uGUI_IButtonReceiver
{
    public const string NAME = "MultiplayerModSync";
    private const int MAX_BUTTONS = 3;

    private static readonly Color accentColor = new(0.34f, 0.84f, 1f, 1f);
    private static readonly Color barBackgroundColor = new(1f, 1f, 1f, 0.12f);

    public static MainMenuModSyncPanel Instance { get; private set; }

    private TextMeshProUGUI titleText;
    private TextMeshProUGUI bodyText;
    private GameObject progressGroup;
    private RectTransform progressFill;
    private TextMeshProUGUI progressText;
    private readonly List<PanelButton> buttons = [];
    private readonly List<Action> buttonActions = [];
    private int cancelButtonIndex = -1;
    private int selectedIndex = -1;

    public void Setup(GameObject savedGamesRef)
    {
        Instance = this;
        Destroy(transform.RequireGameObject("Scroll View"));
        Destroy(GetComponentInChildren<TranslationLiveUpdate>());

        TextMeshProUGUI headerTemplate = transform.RequireTransform("Header").GetComponent<TextMeshProUGUI>();

        GameObject content = new("ModSyncContent", typeof(RectTransform));
        RectTransform contentRect = (RectTransform)content.transform;
        contentRect.SetParent(transform, false);
        contentRect.localPosition = new Vector3(-375, 200, 0);
        contentRect.sizeDelta = new Vector2(420, 560);
        VerticalLayoutGroup layout = content.AddComponent<VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.spacing = 10;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        titleText = CreateText(headerTemplate, contentRect, "Title", 24, FontStyles.Bold, 34);
        bodyText = CreateText(headerTemplate, contentRect, "Body", 15, FontStyles.Normal, 250);
        bodyText.horizontalAlignment = HorizontalAlignmentOptions.Left;
        bodyText.verticalAlignment = VerticalAlignmentOptions.Top;
        bodyText.enableWordWrapping = true;
        bodyText.overflowMode = TextOverflowModes.Ellipsis;

        progressGroup = CreateProgressBar(headerTemplate, contentRect);

        GameObject buttonTemplate = savedGamesRef.RequireGameObject("Scroll View/Viewport/SavedGameAreaContent/NewGame");
        for (int i = 0; i < MAX_BUTTONS; i++)
        {
            buttons.Add(CreateButton(buttonTemplate, contentRect, i));
            buttonActions.Add(null);
        }

        Hide();
    }

    /// <summary>Shows a spinner-like state while the host's mod list is requested and local mods are scanned.</summary>
    public void ShowChecking(string serverName)
    {
        Show(Language.main.Get("Nitrox_ModSync_CheckingTitle"), Language.main.Get("Nitrox_ModSync_Checking").Replace("{SERVER}", serverName));
        SetButtons();
    }

    public void ShowMessage(string title, string body, params (string Label, Action Action, bool IsCancel)[] options)
    {
        Show(title, body);
        SetButtons(options);
    }

    public void ShowProgress(string title, ModDownloadProgress progress, Action cancel)
    {
        if (!progressGroup.activeSelf || titleText.text != title)
        {
            Show(title, "");
            SetButtons((Language.main.Get("Nitrox_Cancel"), cancel, true));
        }
        progressGroup.SetActive(true);
        progressFill.anchorMax = new Vector2(Mathf.Clamp01((float)progress.Fraction), 1);

        string phase = progress.Phase switch
        {
            ModDownloadPhase.VERIFYING => Language.main.Get("Nitrox_ModSync_Verifying"),
            ModDownloadPhase.COMPLETED => Language.main.Get("Nitrox_ModSync_Verified"),
            _ => Language.main.Get("Nitrox_ModSync_Downloading")
        };
        string package = progress.CurrentPackage.Length > 0 ? progress.CurrentPackage.Substring(progress.CurrentPackage.IndexOf('/') + 1) : "";
        string modsRemaining = Language.main.Get("Nitrox_ModSync_ModsRemaining").Replace("{COUNT}", progress.PackagesRemaining.ToString());
        string filesProgress = Language.main.Get("Nitrox_ModSync_FilesProgress").Replace("{DONE}", progress.FilesCompleted.ToString()).Replace("{TOTAL}", progress.FileCount.ToString());
        string message = progress.Message.Length > 0 ? $"\n<color=#FFFFFFAA>{progress.Message}</color>" : "";
        bodyText.text = $"{phase} <b>{package}</b>\n<size=80%><color=#FFFFFFAA>{progress.CurrentFile}</color></size>\n\n{modsRemaining}\n{filesProgress}{message}";
        progressText.text = $"{ModSyncLimits.FormatSize(progress.BytesCompleted)} / {ModSyncLimits.FormatSize(progress.BytesTotal)}   {progress.Fraction * 100:0}%   {ModSyncLimits.FormatSize((long)progress.BytesPerSecond)}/s";
    }

    public void Hide()
    {
        progressGroup.SetActive(false);
        SetButtons();
    }

    private void Show(string title, string body)
    {
        titleText.text = title;
        bodyText.text = body;
        progressGroup.SetActive(false);
        uGUI_MainMenu.main.ShowPrimaryOptions(true);
        MainMenuRightSide.main.OpenGroup(NAME);
    }

    private void SetButtons(params (string Label, Action Action, bool IsCancel)[] options)
    {
        cancelButtonIndex = -1;
        for (int i = 0; i < buttons.Count; i++)
        {
            bool visible = i < options.Length;
            buttons[i].Root.SetActive(visible);
            buttonActions[i] = visible ? options[i].Action : null;
            if (!visible)
            {
                continue;
            }
            buttons[i].Label.text = options[i].Label;
            if (options[i].IsCancel)
            {
                cancelButtonIndex = i;
            }
        }
        selectedIndex = options.Length > 0 ? 0 : -1;
        if (selectedIndex >= 0 && isActiveAndEnabled)
        {
            SelectItem(buttons[selectedIndex].Root);
        }
    }

    private void Invoke(int index)
    {
        if (index < 0 || index >= buttonActions.Count || !buttons[index].Root.activeSelf)
        {
            return;
        }
        buttonActions[index]?.Invoke();
    }

    private static TextMeshProUGUI CreateText(TextMeshProUGUI template, Transform parent, string name, float fontSize, FontStyles style, float height)
    {
        TextMeshProUGUI text = Instantiate(template, parent, false);
        text.name = name;
        foreach (Component component in text.GetComponents<Component>())
        {
            if (component is TranslationLiveUpdate)
            {
                Destroy(component);
            }
        }
        text.text = "";
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.horizontalAlignment = HorizontalAlignmentOptions.Center;
        text.richText = true;
        LayoutElement element = text.gameObject.AddComponent<LayoutElement>();
        element.preferredHeight = height;
        element.minHeight = height;
        return text;
    }

    private GameObject CreateProgressBar(TextMeshProUGUI textTemplate, Transform parent)
    {
        GameObject group = new("Progress", typeof(RectTransform));
        group.transform.SetParent(parent, false);
        group.AddComponent<LayoutElement>().preferredHeight = 44;

        GameObject background = new("Background", typeof(RectTransform), typeof(Image));
        RectTransform backgroundRect = (RectTransform)background.transform;
        backgroundRect.SetParent(group.transform, false);
        backgroundRect.anchorMin = new Vector2(0, 1);
        backgroundRect.anchorMax = new Vector2(1, 1);
        backgroundRect.pivot = new Vector2(0.5f, 1);
        backgroundRect.sizeDelta = new Vector2(0, 8);
        background.GetComponent<Image>().color = barBackgroundColor;

        GameObject fill = new("Fill", typeof(RectTransform), typeof(Image));
        progressFill = (RectTransform)fill.transform;
        progressFill.SetParent(backgroundRect, false);
        progressFill.anchorMin = Vector2.zero;
        progressFill.anchorMax = new Vector2(0, 1);
        progressFill.offsetMin = Vector2.zero;
        progressFill.offsetMax = Vector2.zero;
        fill.GetComponent<Image>().color = accentColor;

        progressText = Instantiate(textTemplate, group.transform, false);
        progressText.name = "ProgressText";
        Destroy(progressText.GetComponent<TranslationLiveUpdate>());
        progressText.fontSize = 14;
        progressText.fontStyle = FontStyles.Normal;
        progressText.horizontalAlignment = HorizontalAlignmentOptions.Center;
        RectTransform textRect = progressText.rectTransform;
        textRect.anchorMin = new Vector2(0, 0);
        textRect.anchorMax = new Vector2(1, 0);
        textRect.pivot = new Vector2(0.5f, 0);
        textRect.sizeDelta = new Vector2(0, 26);
        textRect.anchoredPosition = Vector2.zero;
        return group;
    }

    private PanelButton CreateButton(GameObject template, Transform parent, int index)
    {
        GameObject root = Instantiate(template, parent, false);
        root.name = $"ModSyncButton{index}";
        root.AddComponent<LayoutElement>().preferredHeight = 52;
        Transform buttonTransform = root.RequireTransform("NewGameButton");
        ((RectTransform)buttonTransform).sizeDelta = new Vector2(300, 40);
        Button button = buttonTransform.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => Invoke(index));
        TextMeshProUGUI label = root.GetComponentInChildren<TextMeshProUGUI>();
        if (label.TryGetComponent(out TranslationLiveUpdate translation))
        {
            Destroy(translation);
        }
        return new PanelButton(root, button, label, button.GetComponent<mGUI_Change_Legend_On_Select>());
    }

    bool uGUI_INavigableIconGrid.ShowSelector => false;
    bool uGUI_INavigableIconGrid.EmulateRaycast => false;
    bool uGUI_INavigableIconGrid.SelectItemClosestToPosition(Vector3 worldPos) => false;
    uGUI_INavigableIconGrid uGUI_INavigableIconGrid.GetNavigableGridInDirection(int dirX, int dirY) => null;
    Graphic uGUI_INavigableIconGrid.GetSelectedIcon() => null;

    object uGUI_INavigableIconGrid.GetSelectedItem() => selectedIndex >= 0 ? buttons[selectedIndex].Root : null;

    public bool SelectItemInDirection(int dirX, int dirY)
    {
        int visibleCount = buttons.FindAll(b => b.Root.activeSelf).Count;
        if (visibleCount == 0)
        {
            return false;
        }
        int next = selectedIndex < 0 ? 0 : Mathf.Clamp(selectedIndex + (dirY != 0 ? -dirY : dirX), 0, visibleCount - 1);
        SelectItem(buttons[next].Root);
        return true;
    }

    public bool SelectFirstItem()
    {
        if (buttons.Count > 0 && buttons[0].Root.activeSelf)
        {
            SelectItem(buttons[0].Root);
            return true;
        }
        return false;
    }

    public void SelectItem(object item)
    {
        DeselectItem();
        int index = buttons.FindIndex(b => b.Root == (item as GameObject));
        if (index < 0)
        {
            return;
        }
        selectedIndex = index;
        buttons[index].Button.Select();
        if (buttons[index].Legend)
        {
            buttons[index].Legend.SyncLegendBarToGUISelection();
        }
    }

    public void DeselectItem()
    {
        if (EventSystem.current)
        {
            EventSystem.current.SetSelectedGameObject(null);
        }
        foreach (PanelButton button in buttons)
        {
            uGUI_BasicColorSwap colorSwap = button.Root.GetComponentInChildren<uGUI_BasicColorSwap>();
            if (colorSwap)
            {
                colorSwap.makeTextWhite();
            }
        }
        uGUI_LegendBar.ClearButtons();
    }

    public bool OnButtonDown(GameInput.Button button)
    {
        switch (button)
        {
            case GameInput.Button.UISubmit:
                Invoke(selectedIndex);
                return true;
            case GameInput.Button.UICancel:
                Invoke(cancelButtonIndex);
                return true;
            default:
                return false;
        }
    }

    private sealed record PanelButton(GameObject Root, Button Button, TextMeshProUGUI Label, mGUI_Change_Legend_On_Select Legend);
}
