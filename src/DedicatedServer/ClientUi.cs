// The client's UI, all made from the game's own UI so it looks like the game:
//
//  * The Join Game page: saved servers at the top of its list (above Steam friends), with their
//    name, description, players and whether they're up, and a Refresh button; below, "Join by
//    address": a Name box, an address box, Join, and Save (pin the server to the list).
//  * When a server turns you away, the game's own "You have been disconnected" popup says why,
//    with the mods you are missing. When it wants a password, that popup asks for it and joins again.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Managers.Interface;
using TMPro;
using UI.MainMenu;
using UI.MainMenu.Common;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

namespace DedicatedServer
{
    internal static class ClientUi
    {
        private const string PanelName = "DedicatedServer.JoinByAddress";
        private const string RowName = "DedicatedServer.SavedServer";

        private static MenuJoinGame _page;
        private static TMP_InputField _field, _name;
        private static TextMeshProUGUI _hint;
        private static Button _save, _buttonTemplate;
        private static TMP_InputField _fieldTemplate;
        private static Transform _header;
        private static float _nextLook;
        private static int _drawnVersion = -1;
        private static readonly List<ServerRow> _rows = new List<ServerRow>();
        public static string Selected;            // address of the saved server picked in the list
        public static bool Built;
        public static string BuildError;

        public static void Init(HarmonyLib.Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(MainMenuConnectionErrorActivator), "Start"),
                    postfix: new HarmonyMethod(typeof(ClientUi).GetMethod("ErrorActivatorPostfix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                h.Patch(AccessTools.Method(typeof(MenuJoinGame), "ReloadButtons"),
                    prefix: new HarmonyMethod(typeof(ClientUi).GetMethod("ReloadButtonsPrefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            }
            catch (Exception e) { Log.Error("could not patch the main menu: " + e.Message); }
        }

        // The Join page lists Steam friends; without Steam running it threw and never finished
        // opening. Without Steam there are no friends to list, only saved servers and the address box.
        private static bool ReloadButtonsPrefix(MenuJoinGame __instance)
        {
            if (SteamManager.Initialized) return true;
            try
            {
                GameObject side = Traverse.Create(__instance).Field("sideMenu").GetValue<GameObject>();
                if (side != null) side.SetActive(false);
            }
            catch { }
            return false;
        }

        // ---- the popup -----------------------------------------------------------------------

        public static string PopupState = "";
        public static ClosablePopup Shown;                      // the turned-away popup on screen now, if any       // for the probe: "", "rejected", "password"
        private static TMP_InputField _popupPass;

        private static void ErrorActivatorPostfix(MainMenuConnectionErrorActivator __instance)
        {
            if (!Client.LastVerdict.HasValue || Client.LastVerdict.Value.Accepted || Client.RejectionShown) return;
            Client.RejectionShown = true;
            DsVerdict v = Client.LastVerdict.Value;
            try
            {
                ClosablePopup popup = Traverse.Create(__instance).Field("menuPopup").GetValue<ClosablePopup>();
                LocalizeStringEvent loc = Traverse.Create(popup).Field("stringMessage").GetValue<LocalizeStringEvent>();
                TMP_Text text = loc != null ? loc.GetComponent<TMP_Text>() : popup.GetComponentInChildren<TMP_Text>(true);
                if (loc != null) loc.enabled = false;       // or the game's localized text comes back on top of ours
                text.enableAutoSizing = true;
                text.fontSizeMin = 18;
                text.fontSizeMax = text.fontSize;
                ResetPopup(popup);
                if (SavedServers.NeedsPassword(v)) { AskPassword(popup, text, SavedServers.WrongPassword(v)); }
                else
                {
                    text.text = RejectionText(v);
                    PopupState = "rejected";
                    ModInstall.AddToPopup(popup, text, v);      // the server can send the missing mods
                    if (ModInstall.State != "offered") ModsOff.AddToPopup(popup, text, v);   // or the player can turn off the ones it doesn't allow
                }
                popup.gameObject.SetActive(true);
                Shown = popup;                                  // (JoinWatch keeps the mouse free while it is up)
                Client.RejectionPopupText = text.text;
            }
            catch (Exception e) { Log.Warn("could not show why the server turned us away: " + e.Message); }
        }

        private static Button.ButtonClickedEvent _okClick;
        private static Vector2 _okPos, _okSize, _okPivot;
        private static string _okText;

        /// <summary>
        /// The game's one disconnect popup is reused every time: buttons added to it last time are taken away and its OK
        /// button is put back as the game made it (place, size, label, what it does) before this time's are added.
        /// </summary>
        private static void ResetPopup(ClosablePopup popup)
        {
            Button ok = popup.GetComponentsInChildren<Button>(true).FirstOrDefault(b => !b.name.StartsWith("DedicatedServer."));
            foreach (Button b in popup.GetComponentsInChildren<Button>(true).Where(b => b.name.StartsWith("DedicatedServer.")).ToList())
                UnityEngine.Object.Destroy(b.gameObject);
            foreach (TMP_InputField f in popup.GetComponentsInChildren<TMP_InputField>(true).Where(f => f.name.StartsWith("DedicatedServer.")).ToList())
                UnityEngine.Object.Destroy(f.gameObject);
            if (ModInstall.State == "offered") ModInstall.State = "";
            if (ModsOff.State == "offered") ModsOff.State = "";
            if (ok == null) return;
            RectTransform r = (RectTransform)ok.transform;
            TMP_Text label = ok.GetComponentInChildren<TMP_Text>(true);
            if (_okClick == null) { _okClick = ok.onClick; _okPos = r.anchoredPosition; _okSize = r.sizeDelta; _okPivot = r.pivot; _okText = label != null ? label.text : ""; return; }
            ok.onClick = _okClick;
            r.pivot = _okPivot; r.anchoredPosition = _okPos; r.sizeDelta = _okSize;
            ok.interactable = true;
            if (label != null)
            {
                UnityEngine.Localization.Components.LocalizeStringEvent l = label.GetComponent<UnityEngine.Localization.Components.LocalizeStringEvent>();
                if (l != null) { l.enabled = true; l.RefreshString(); } else label.text = _okText;
            }
        }

        /// <summary>The disconnect popup, turned into "this server needs a password": a box, Join and Cancel.</summary>
        private static void AskPassword(ClosablePopup popup, TMP_Text text, bool wrong)
        {
            SavedServer s = SavedServers.Get(Client.LastAddress.Value, false);
            string who = s != null && s.Name.Length > 0 ? s.Name : Client.LastAddress.Value;
            text.text = (wrong ? "Wrong password for " : "") + who + (wrong ? ". Try again:" : " needs a password:");
            PopupState = "password";
            Button ok = popup.GetComponentsInChildren<Button>(true).FirstOrDefault();
            TMP_InputField template = Resources.FindObjectsOfTypeAll<TMP_InputField>()
                .FirstOrDefault(f => f.gameObject.scene == popup.gameObject.scene && f.GetComponentInParent<CreateWorldModal>(true) != null);
            if (ok == null || template == null) { text.text += "\n(type it in the Join page's address box as address#password)"; return; }

            RectTransform okr = (RectTransform)ok.transform;
            _popupPass = UnityEngine.Object.Instantiate(template, okr.parent, false);
            Strip(_popupPass.gameObject);
            _popupPass.name = "DedicatedServer.Password";
            RectTransform pr = (RectTransform)_popupPass.transform;
            // across the popup, just above the buttons; the popup grows by that much
            RectTransform par = (RectTransform)okr.parent;
            Vector3[] oc = new Vector3[4];
            okr.GetWorldCorners(oc);
            float okBottom = par.InverseTransformPoint(oc[0]).y - par.rect.yMin, okTop = par.InverseTransformPoint(oc[1]).y - par.rect.yMin;
            float okH = Mathf.Max(20f, okTop - okBottom);
            par.sizeDelta = new Vector2(par.sizeDelta.x, par.sizeDelta.y + okH * 1.6f);
            par.anchoredPosition = par.anchoredPosition - new Vector2(0f, okH * 0.8f * (1f - par.pivot.y) * 0f);
            par.GetWorldCorners(oc);
            okr.GetWorldCorners(oc);
            okBottom = par.InverseTransformPoint(oc[0]).y - par.rect.yMin; okTop = par.InverseTransformPoint(oc[1]).y - par.rect.yMin;
            pr.anchorMin = new Vector2(0.07f, 0f); pr.anchorMax = new Vector2(0.93f, 0f); pr.pivot = new Vector2(0.5f, 0f);
            pr.sizeDelta = new Vector2(0f, okH * 1.15f);
            pr.anchoredPosition = new Vector2(0f, okTop + okH * 0.3f);
            _popupPass.characterLimit = 64;
            _popupPass.contentType = TMP_InputField.ContentType.Password;
            _popupPass.lineType = TMP_InputField.LineType.SingleLine;
            _popupPass.onEndEdit.RemoveAllListeners(); _popupPass.onSubmit.RemoveAllListeners(); _popupPass.onValueChanged.RemoveAllListeners();
            _popupPass.text = "";
            if (_popupPass.placeholder is TMP_Text ph) { ph.text = "Password"; ph.font = _popupPass.textComponent.font; ph.fontSize = _popupPass.textComponent.fontSize * 0.8f; ph.enableAutoSizing = false; ph.color = new Color(1f, 1f, 1f, 0.35f); }
            _popupPass.onSubmit.AddListener(t => JoinWithPassword(popup));

            // OK becomes Join; a copy of it beside it is Cancel
            Button cancel = UnityEngine.Object.Instantiate(ok, okr.parent, false);
            Strip(cancel.gameObject);
            cancel.name = "DedicatedServer.Cancel";
            cancel.onClick = new Button.ButtonClickedEvent();
            cancel.onClick.AddListener(() => { PopupState = ""; popup.Close(); });
            SetLabel(cancel, "Cancel");
            RectTransform cr = (RectTransform)cancel.transform;
            float gap = okr.sizeDelta.x * 0.55f + 10f;
            cr.anchoredPosition = okr.anchoredPosition + new Vector2(gap, 0f);
            okr.anchoredPosition = okr.anchoredPosition - new Vector2(gap, 0f);
            ok.onClick = new Button.ButtonClickedEvent();
            ok.onClick.AddListener(() => JoinWithPassword(popup));
            SetLabel(ok, "Join");
            Strip(ok.gameObject);
            _popupPass.Select();
            _popupPass.ActivateInputField();
        }

        private static void JoinWithPassword(ClosablePopup popup)
        {
            string p = _popupPass != null ? _popupPass.text : "";
            if (string.IsNullOrEmpty(p)) return;
            PopupState = "";
            popup.Close();
            string err = Client.Join(Client.LastAddress.Value, p);
            if (err != null) { Client.JoinError = err; ShowHint(err); }
        }

        /// <summary>For the probe: type into the password popup and press Join.</summary>
        public static bool TypePasswordInPopup(string p)
        {
            if (_popupPass == null || PopupState != "password") return false;
            _popupPass.text = p;
            _popupPass.onSubmit.Invoke(p);
            return true;
        }

        public static string RejectionText(DsVerdict v)
        {
            List<string> missing = (v.Missing ?? "").Split('\n').Where(x => x.Trim().Length > 0 && !x.StartsWith("#"))
                .Select(x => { string[] p = x.Split('\t'); return p[0] + (p.Length > 1 && p[1].Length > 0 ? " " + p[1] : ""); }).ToList();
            string s = "The server turned you away.\n" + (v.Message ?? "");
            if (missing.Count > 0 && !(v.Message ?? "").Contains(missing[0]))
                s += "\nMissing: " + string.Join(", ", missing.ToArray());
            if (missing.Count > 0) s += "\n<size=75%>Put the same versions in your Mods folder, then join again.</size>";
            if ((v.Missing ?? "").Contains(Gate.CodeExtraMods)) s += "\n<size=75%>Take them out of your Mods folder (or ask the server's admin to allow them), then join again.</size>";
            return s;
        }

        // ---- the Join page -----------------------------------------------------------------

        public static void Update()
        {
            try { ModInstall.Update(); } catch (Exception e) { Log.Once("modinstall", "mod download: " + e.Message); }
            if (Time.realtimeSinceStartup < _nextLook) { RedrawIfChanged(); return; }
            _nextLook = Time.realtimeSinceStartup + 0.2f;
            if (_page == null) _page = UnityEngine.Object.FindFirstObjectByType<MenuJoinGame>();
            if (_page == null) { _field = null; _rows.Clear(); return; }
            if (_page.transform.Find(PanelName) == null)
            {
                try { Build(_page); Built = true; BuildError = null; SavedServers.AskAll(); _drawnVersion = -1; }
                catch (Exception e) { BuildError = e.Message; Log.Once("joinbox", "could not add 'Join by address' to the Join page: " + e); _nextLook = Time.realtimeSinceStartup + 5f; }
            }
            RedrawIfChanged();
        }

        private static void Build(MenuJoinGame page)
        {
            RectTransform root = (RectTransform)page.transform;
            TMP_Text header = page.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.GetComponentInParent<Button>() == null);
            Button joinButton = Traverse.Create(page).Field("sideMenu").GetValue<GameObject>().GetComponentInChildren<Button>(true);
            TMP_InputField fieldTemplate = Resources.FindObjectsOfTypeAll<TMP_InputField>()
                .FirstOrDefault(f => f.gameObject.scene == page.gameObject.scene && f.GetComponentInParent<CreateWorldModal>(true) != null);
            if (header == null || joinButton == null || fieldTemplate == null)
                throw new Exception("the page's parts weren't found (header " + (header != null) + ", button " + (joinButton != null) + ", text box " + (fieldTemplate != null) + ")");
            _buttonTemplate = joinButton; _fieldTemplate = fieldTemplate;

            GameObject panel = new GameObject(PanelName, typeof(RectTransform));
            RectTransform pr = (RectTransform)panel.transform;
            pr.SetParent(root, false);
            pr.anchorMin = pr.anchorMax = pr.pivot = new Vector2(0f, 0f);
            // left edge in line with the page's header; low on the page, where it is empty
            Vector3[] corners = new Vector3[4];
            ((RectTransform)header.transform).GetWorldCorners(corners);
            Vector3 local = root.InverseTransformPoint(corners[0]);
            pr.anchoredPosition = new Vector2(local.x - root.rect.xMin, 180f);
            pr.sizeDelta = new Vector2(1500f, 560f);

            TMP_Text title = UnityEngine.Object.Instantiate(header, pr, false);
            Strip(title.gameObject);
            title.name = "Title";
            title.text = "Join by address";
            Place(title.rectTransform, 0f, 440f, 1500f, 100f);
            title.alignment = TextAlignmentOptions.MidlineLeft;

            // Name: yours on the server. Steam's name to start with; what you type is kept.
            TMP_Text nameLabel = UnityEngine.Object.Instantiate(header, pr, false);
            Strip(nameLabel.gameObject);
            nameLabel.name = "NameLabel";
            nameLabel.text = "Name";
            nameLabel.fontSize = header.fontSize * 0.7f;
            nameLabel.enableWordWrapping = false;
            nameLabel.alignment = TextAlignmentOptions.MidlineLeft;
            Place(nameLabel.rectTransform, 0f, 320f, 200f, 100f);
            _name = UnityEngine.Object.Instantiate(fieldTemplate, pr, false);
            Strip(_name.gameObject);
            _name.name = "PlayerName";
            Place((RectTransform)_name.transform, 210f, 320f, 830f, 100f);
            _name.characterLimit = Client.MaxName;
            _name.contentType = TMP_InputField.ContentType.Standard;
            _name.lineType = TMP_InputField.LineType.SingleLine;
            _name.onEndEdit.RemoveAllListeners();
            _name.onSubmit.RemoveAllListeners();
            _name.onValueChanged.RemoveAllListeners();
            _name.text = Client.DefaultName();
            if (_name.placeholder is TMP_Text nph) { nph.text = "Your name"; nph.font = _name.textComponent.font; nph.fontSize = _name.textComponent.fontSize * 0.8f; nph.color = new Color(1f, 1f, 1f, 0.35f); }
            _name.onEndEdit.AddListener(SaveName);

            _field = UnityEngine.Object.Instantiate(fieldTemplate, pr, false);
            Strip(_field.gameObject);
            _field.name = "Address";
            Place((RectTransform)_field.transform, 0f, 200f, 1040f, 100f);
            _field.characterLimit = 120;
            _field.contentType = TMP_InputField.ContentType.Standard;
            _field.lineType = TMP_InputField.LineType.SingleLine;
            _field.onEndEdit.RemoveAllListeners();
            _field.onSubmit.RemoveAllListeners();
            _field.onValueChanged.RemoveAllListeners();
            _field.text = Client.LastAddress.Value ?? "";
            if (_field.placeholder is TMP_Text ph)
            {
                ph.text = "123.45.67.89:7777 or a playit.gg address";
                ph.font = _field.textComponent.font;
                ph.fontSize = _field.textComponent.fontSize * 0.8f;
                ph.color = new Color(1f, 1f, 1f, 0.35f);
            }
            _field.onSubmit.AddListener(s => DoJoin());
            _field.onValueChanged.AddListener(s => UpdateSaveLabel());

            Button b = CopyButton(joinButton, pr, "JoinByAddress", "Join", DoJoin);
            Place((RectTransform)b.transform, 1080f, 200f, 380f, 100f);
            _save = CopyButton(joinButton, pr, "SaveServer", "Save", () =>
            {
                string addr = (_field.text ?? "").Trim();
                string host, err; int port;
                if (!Client.ParseAddress(addr, out host, out port, out err)) { ShowHint(err); return; }
                SavedServer s = SavedServers.Get(addr, false);
                if (s != null && s.Pinned) { SavedServers.Unpin(s); ShowHint("Removed from your saved servers."); }
                else { SavedServers.Pin(addr); ShowHint("Saved: it's at the top of the list now."); }
                UpdateSaveLabel();
            });
            Place((RectTransform)_save.transform, 1080f, 320f, 380f, 100f);
            UpdateSaveLabel();

            GameObject hintGo = new GameObject("Hint", typeof(RectTransform));
            hintGo.transform.SetParent(pr, false);
            _hint = hintGo.AddComponent<TextMeshProUGUI>();
            _hint.font = _field.textComponent.font;
            _hint.fontSize = _field.textComponent.fontSize * 0.75f;
            _hint.enableWordWrapping = true;
            _hint.alignment = TextAlignmentOptions.TopLeft;
            Place(_hint.rectTransform, 0f, 60f, 1500f, 130f);
            ShowHint(Client.JoinError);

            // the list's heading covers both now, and Refresh asks every saved server again
            LocalizeStringEvent hloc = header.GetComponent<LocalizeStringEvent>();
            if (hloc != null) hloc.enabled = false;
            header.text = "Servers and friends in game";
            _header = header.transform;
            // the heading goes above the list (the list sits before it in the page's layout, so rows would push it down)
            Transform wrapper = Traverse.Create(page).Field("buttonsWrapper").GetValue<Transform>();
            Transform holder = wrapper;
            while (holder != null && holder.parent != header.transform.parent) holder = holder.parent;   // the part of the heading's layout that holds the list
            if (holder != null && header.transform.GetSiblingIndex() > holder.GetSiblingIndex())
                header.transform.SetSiblingIndex(holder.GetSiblingIndex());
            Log.Msg("Join page: heading under " + header.transform.parent.name + " (" + header.transform.GetSiblingIndex() + "), list in " + (holder != null ? holder.name + " (" + holder.GetSiblingIndex() + ")" : "another branch: " + (wrapper != null ? wrapper.parent.name : "-")));
            Button refresh = CopyButton(joinButton, (RectTransform)header.transform, "DedicatedServer.Refresh", "Refresh", () => { SavedServers.AskAll(); ShowHint("Asking your saved servers..."); });
            RectTransform rr = (RectTransform)refresh.transform;
            LayoutElement rle = refresh.GetComponent<LayoutElement>() ?? refresh.gameObject.AddComponent<LayoutElement>();
            rle.ignoreLayout = true;
            rr.anchorMin = rr.anchorMax = new Vector2(0f, 0.5f);
            rr.pivot = new Vector2(0f, 0.5f);
            rr.anchoredPosition = new Vector2(header.preferredWidth + 30f, 0f);
            rr.sizeDelta = new Vector2(260f, 70f);
            Log.Msg("added saved servers and 'Join by address' to the Join page");
        }

        private static Button CopyButton(Button template, RectTransform parent, string name, string label, UnityEngine.Events.UnityAction click)
        {
            Button b = UnityEngine.Object.Instantiate(template, parent, false);
            Strip(b.gameObject);
            b.name = name;
            b.gameObject.SetActive(true);
            b.onClick = new Button.ButtonClickedEvent();    // a copy must not run the page's own (Steam) join
            b.onClick.AddListener(click);
            SetLabel(b, label);
            return b;
        }

        private static void SetLabel(Button b, string label)
        {
            TMP_Text t = b.GetComponentInChildren<TMP_Text>(true);
            if (t == null) return;
            LocalizeStringEvent l = t.GetComponent<LocalizeStringEvent>();
            if (l != null) l.enabled = false;
            t.text = label;
        }

        private static void UpdateSaveLabel()
        {
            if (_save == null || _field == null) return;
            SavedServer s = SavedServers.Get(_field.text, false);
            SetLabel(_save, s != null && s.Pinned ? "Unsave" : "Save");
        }

        // ---- the saved servers in the list -------------------------------------------------

        private static void RedrawIfChanged()
        {
            if (_page == null || _field == null) return;
            if (_drawnVersion == SavedServers.Version && _rows.All(r => r != null)) return;
            _drawnVersion = SavedServers.Version;
            try { DrawRows(); } catch (Exception e) { Log.Once("rows", "could not list the saved servers: " + e); }
        }

        private static void DrawRows()
        {
            Transform wrapper = Traverse.Create(_page).Field("buttonsWrapper").GetValue<Transform>();
            FriendGameButton prefab = Traverse.Create(_page).Field("buttonPrefab").GetValue<FriendGameButton>();
            if (wrapper == null || prefab == null) return;
            List<SavedServer> list = SavedServers.Pinned.ToList();
            _rows.RemoveAll(r => r == null);
            foreach (ServerRow r in _rows.ToList())
                if (!list.Any(s => s.Address == r.Server.Address)) { _rows.Remove(r); UnityEngine.Object.Destroy(r.gameObject); }
            int i = 0;
            foreach (SavedServer s in list)
            {
                ServerRow row = _rows.FirstOrDefault(r => r.Server.Address == s.Address);
                if (row == null)
                {
                    FriendGameButton copy = UnityEngine.Object.Instantiate(prefab, wrapper, false);
                    copy.name = RowName;
                    row = copy.gameObject.AddComponent<ServerRow>();
                    row.Setup(copy, s);
                    _rows.Add(row);
                }
                row.Server = s;
                // under the list's heading when the heading is part of the list, else first
                Transform h = _header;
                while (h != null && h.parent != wrapper) h = h.parent;      // the heading's block, when it is part of the list
                int top = h != null ? h.GetSiblingIndex() + 1 : 0;
                if (row.transform.GetSiblingIndex() < top) top--;
                row.transform.SetSiblingIndex(top + i++);
                row.Draw();
            }
        }

        internal static void Pick(ServerRow row, bool join)
        {
            Selected = row.Server.Address;
            if (_field != null) { _field.text = row.Server.Address; UpdateSaveLabel(); }
            foreach (ServerRow r in _rows) if (r != null) r.Draw();
            if (join) { DoJoin(); return; }
            ShowHint(row.Server.Banned ? "You are banned from " + Title(row.Server) + "." : Title(row.Server) + ": press Join (or double-click it).");
        }

        /// <summary>The row's Play button: straight in, no Join button needed.</summary>
        internal static void PlayRow(ServerRow row) { Pick(row, true); }

        // ---- changing a saved server's address, in its row ------------------------------------

        private static ServerRow _editing;
        private static GameObject _editor;
        private static TMP_InputField _editField;
        public static ServerRow Editing { get { return _editing != null && _editor != null ? _editing : null; } }

        internal static void EditRow(ServerRow row)
        {
            CloseEditor();
            if (_fieldTemplate == null || _buttonTemplate == null) return;
            _editing = row;
            Rect r = row.TextArea;
            _editor = new GameObject("DedicatedServer.EditAddress", typeof(RectTransform));
            RectTransform er = (RectTransform)_editor.transform;
            er.SetParent(row.transform, false);
            er.anchorMin = er.anchorMax = er.pivot = new Vector2(0f, 0f);
            er.anchoredPosition = r.position; er.sizeDelta = r.size;
            float h = r.height * 0.62f, bw = h * 2.4f;
            _editField = UnityEngine.Object.Instantiate(_fieldTemplate, er, false);
            Strip(_editField.gameObject);
            _editField.name = "Address";
            Place((RectTransform)_editField.transform, 0f, (r.height - h) / 2f, Mathf.Max(80f, r.width - 2f * bw - 24f), h);
            _editField.characterLimit = 120;
            _editField.contentType = TMP_InputField.ContentType.Standard;
            _editField.lineType = TMP_InputField.LineType.SingleLine;
            _editField.onEndEdit.RemoveAllListeners(); _editField.onSubmit.RemoveAllListeners(); _editField.onValueChanged.RemoveAllListeners();
            _editField.text = row.Server.Address;
            if (_editField.placeholder is TMP_Text ph) { ph.text = "New address"; ph.color = new Color(1f, 1f, 1f, 0.35f); }
            _editField.onSubmit.AddListener(t => SaveEdit());
            Button ok = CopyButton(_buttonTemplate, er, "SaveAddress", "Save", () => SaveEdit());
            Place((RectTransform)ok.transform, r.width - 2f * bw - 12f, (r.height - h) / 2f, bw, h);
            Button cancel = CopyButton(_buttonTemplate, er, "CancelEdit", "Cancel", CloseEditor);
            Place((RectTransform)cancel.transform, r.width - bw, (r.height - h) / 2f, bw, h);
            row.Draw();
            _editField.Select(); _editField.ActivateInputField();
            ShowHint("Change the address of " + Title(row.Server) + " (its password is kept). Enter or Save.");
        }

        /// <summary>The Save button (and Enter): the typed address replaces the saved one. Null when done, else why not.</summary>
        internal static string SaveEdit()
        {
            if (_editing == null || _editField == null) return "not editing";
            string old = _editing.Server.Address;
            string err = SavedServers.ChangeAddress(_editing.Server, _editField.text);
            if (err != null) { ShowHint(err); return err; }
            if (Selected == old) Selected = _editing.Server.Address;
            if (_field != null && SavedServers.Key(_field.text) == SavedServers.Key(old)) { _field.text = _editing.Server.Address; UpdateSaveLabel(); }
            ShowHint(Title(_editing.Server) + " is saved as " + _editing.Server.Address + " now.");
            CloseEditor();
            return null;
        }

        internal static void TypeInEditor(string text) { if (_editField != null) _editField.text = text; }

        internal static void CloseEditor()
        {
            ServerRow was = _editing;
            if (_editor != null) UnityEngine.Object.Destroy(_editor);
            _editor = null; _editField = null; _editing = null;
            if (was != null) was.Draw();
        }

        internal static void Remove(ServerRow row)
        {
            SavedServers.Unpin(row.Server);
            if (Selected == row.Server.Address) Selected = null;
            ShowHint("Removed " + Title(row.Server) + " from your saved servers.");
            UpdateSaveLabel();
        }

        internal static string Title(SavedServer s) { return s.Name.Length > 0 ? s.Name : s.Address; }

        // ---- the address box ---------------------------------------------------------------

        private static void SaveName(string typed)
        {
            string n = Client.CleanName(typed);
            if (_name != null && _name.text != n) _name.text = n.Length > 0 ? n : Client.DefaultName();
            Client.PlayerName.Value = n;
            try { MelonLoader.MelonPreferences.Save(); } catch { }
            try { Identity.NameChanged(n); } catch { }
        }

        private static void DoJoin()
        {
            if (_field == null) return;
            if (_name != null) SaveName(_name.text);
            SavedServer s = SavedServers.Get(_field.text, false);
            if (s != null && s.Banned && s.Pinned) { ShowHint("You are banned from " + Title(s) + "."); return; }
            string err = Client.Join(_field.text);
            Client.JoinError = err;
            ShowHint(err);
        }

        private static void ShowHint(string error)
        {
            if (_hint == null) return;
            bool bad = error != null && (error.StartsWith("You are banned") || error.Contains("isn't") || error.Contains("Can't") || error.Contains("Type an") || error.Contains("Missing"));
            _hint.color = bad ? new Color(1f, 0.55f, 0.45f, 1f) : new Color(0.85f, 0.9f, 0.95f, 0.8f);
            _hint.text = error ?? "An IP address or host name, with :port if it isn't " + Client.DefaultPort + ". Works with port forwarding, playit.gg and other tunnels. Save puts it at the top of the list.";
        }

        /// <summary>Copies keep the game's look, not its behaviour: no localization overwriting our text, no selection sounds wired to the page.</summary>
        private static void Strip(GameObject go)
        {
            foreach (LocalizeStringEvent l in go.GetComponentsInChildren<LocalizeStringEvent>(true)) UnityEngine.Object.DestroyImmediate(l);
            foreach (LayoutElement l in go.GetComponents<LayoutElement>()) l.ignoreLayout = true;
        }

        private static void Place(RectTransform r, float x, float y, float w, float h)
        {
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 0f);
            r.anchoredPosition = new Vector2(x, y);
            r.sizeDelta = new Vector2(w, h);
        }

        // ---- for the probe -----------------------------------------------------------------

        public static bool PanelPresent()
        {
            MenuJoinGame page = UnityEngine.Object.FindFirstObjectByType<MenuJoinGame>();
            return page != null && page.transform.Find(PanelName) != null && page.transform.Find(PanelName).gameObject.activeInHierarchy;
        }

        public static bool TypeName(string name)
        {
            if (_name == null) return false;
            _name.text = name;
            _name.onEndEdit.Invoke(_name.text);      // what leaving the box does
            return true;
        }

        public static string NameShown { get { return _name != null ? _name.text : null; } }

        public static bool TypeAndJoin(string address)
        {
            if (_field == null) return false;
            _field.text = address;
            _field.onSubmit.Invoke(_field.text);     // what pressing Enter in the box does
            return true;
        }

        public static bool PressSave(string address)
        {
            if (_field == null || _save == null) return false;
            _field.text = address;
            _save.onClick.Invoke();
            return true;
        }

        public static List<ServerRow> Rows { get { _rows.RemoveAll(r => r == null); return _rows; } }

        public static RectTransform Panel()
        {
            MenuJoinGame page = UnityEngine.Object.FindFirstObjectByType<MenuJoinGame>();
            return page != null ? page.transform.Find(PanelName) as RectTransform : null;
        }
    }

    /// <summary>
    /// One saved server in the Join page's list (a copy of the game's friend button): its icon, name, state and description.
    /// Pointing at it shows Play and Settings over the icon at once (Play joins, Settings changes its address); the x at the
    /// right end, inside the box, takes it off the list. A click picks it, a double-click joins.
    /// </summary>
    internal class ServerRow : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public SavedServer Server;
        private Image _bg;
        private TMP_Text _text;
        private RawImage _dot;
        private bool _hover;
        private float _lastClick;
        private RectTransform _over, _play, _gear, _x;
        private Image _overBg, _playImg, _gearImg, _gearBack, _xBack;
        private TMP_Text _xText;
        private Rect _laidFor;
        public Rect TextArea;                    // in the row's own units from its bottom-left (the address editor goes here)

        private static Sprite _triangle, _cog, _round;

        public void Setup(FriendGameButton b, SavedServer s)
        {
            Server = s;
            _bg = Traverse.Create(b).Field("backgroundImage").GetValue<Image>();
            _text = Traverse.Create(b).Field("playerNameText").GetValue<TMP_Text>();
            _dot = Traverse.Create(b).Field("avatarImage").GetValue<RawImage>();
            // its own click/hover wiring points at the friend button; ours replaces it
            foreach (EventTrigger t in GetComponentsInChildren<EventTrigger>(true)) t.triggers.Clear();
            foreach (Button x in GetComponentsInChildren<Button>(true)) x.onClick = new Button.ButtonClickedEvent();
            DestroyImmediate(b);
            // the parts are placed by Layout() below, not by the friend button's own layout
            foreach (LayoutGroup g in GetComponentsInChildren<LayoutGroup>(true)) g.enabled = false;
            foreach (ContentSizeFitter f in GetComponentsInChildren<ContentSizeFitter>(true)) if (f.gameObject != gameObject) f.enabled = false;
            if (_text != null)
            {
                _text.richText = true; _text.enableWordWrapping = false; _text.overflowMode = TextOverflowModes.Ellipsis;
                _text.enableAutoSizing = false;
                _text.alignment = TextAlignmentOptions.MidlineLeft;
            }
            // two lines: name and state, then the description
            RectTransform me = (RectTransform)transform;
            float h = Mathf.Max(me.rect.height, 60f) * 1.7f;
            LayoutElement le = GetComponent<LayoutElement>() ?? gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = false; le.minHeight = h; le.preferredHeight = h;
            me.sizeDelta = new Vector2(me.sizeDelta.x, h);
            if (_dot != null) { _dot.texture = Texture2D.whiteTexture; _dot.raycastTarget = true; }
            MakeSprites();

            // Play and Settings over the icon, shown while the pointer is on the row
            _over = NewRect("Hover", transform);
            _overBg = _over.gameObject.AddComponent<Image>();
            _overBg.color = new Color(0.02f, 0.05f, 0.09f, 0.62f);
            _play = NewRect("Play", _over);
            _playImg = _play.gameObject.AddComponent<Image>();
            _playImg.sprite = _triangle; _playImg.preserveAspect = true;
            _play.gameObject.AddComponent<RowButton>().Init(this, RowButton.Kind.Play, _playImg);
            _gear = NewRect("Settings", _over);
            _gearBack = _gear.gameObject.AddComponent<Image>();
            _gearBack.sprite = _round; _gearBack.color = new Color(0.02f, 0.05f, 0.09f, 0.85f);
            RectTransform cog = NewRect("Cog", _gear);
            cog.anchorMin = new Vector2(0.12f, 0.12f); cog.anchorMax = new Vector2(0.88f, 0.88f); cog.offsetMin = cog.offsetMax = Vector2.zero;
            _gearImg = cog.gameObject.AddComponent<Image>();
            _gearImg.sprite = _cog; _gearImg.preserveAspect = true; _gearImg.raycastTarget = false;
            _gear.gameObject.AddComponent<RowButton>().Init(this, RowButton.Kind.Settings, _gearImg);
            _over.gameObject.SetActive(false);

            // the x: inside the box, at its right end
            _x = NewRect("Remove", transform);
            _xBack = _x.gameObject.AddComponent<Image>();
            _xBack.sprite = _round; _xBack.color = new Color(1f, 1f, 1f, 0f);
            RectTransform xt = NewRect("X", _x);
            xt.anchorMin = Vector2.zero; xt.anchorMax = Vector2.one; xt.offsetMin = xt.offsetMax = Vector2.zero;
            TextMeshProUGUI xtext = xt.gameObject.AddComponent<TextMeshProUGUI>();
            xtext.font = _text != null ? _text.font : xtext.font;
            xtext.text = "x"; xtext.alignment = TextAlignmentOptions.Center; xtext.raycastTarget = false;
            xtext.color = new Color(1f, 1f, 1f, 0.6f);
            _xText = xtext;
            _x.gameObject.AddComponent<RowButton>().Init(this, RowButton.Kind.Remove, null);
            Layout();
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform r = (RectTransform)go.transform;
            r.anchorMin = r.anchorMax = r.pivot = Vector2.zero;
            return r;
        }

        private static void Put(RectTransform r, Rect at)
        {
            r.anchorMin = r.anchorMax = r.pivot = Vector2.zero;
            r.anchoredPosition = at.position; r.sizeDelta = at.size;
        }

        /// <summary>Where the box is drawn, in the row's units from its bottom-left (the row and its box needn't match).</summary>
        public Rect Box
        {
            get
            {
                RectTransform me = (RectTransform)transform;
                if (_bg == null || _bg.transform == transform) return new Rect(0f, 0f, me.rect.width, me.rect.height);
                Vector3[] c = new Vector3[4];
                _bg.rectTransform.GetWorldCorners(c);
                Vector2 a = (Vector2)me.InverseTransformPoint(c[0]) - me.rect.min, b = (Vector2)me.InverseTransformPoint(c[2]) - me.rect.min;
                return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            }
        }

        /// <summary>The icon square, the text, the x and the hover buttons, from where the box is (redone when it moves or resizes).</summary>
        private void Layout()
        {
            Rect box = Box;
            if (box == _laidFor || box.width < 10f || box.height < 10f) return;
            _laidFor = box;
            float pad = Mathf.Round(box.height * 0.1f);
            float icon = box.height - 2f * pad;
            Rect iconAt = new Rect(box.xMin + pad, box.yMin + pad, icon, icon);
            if (_dot != null) Put(_dot.rectTransform, iconAt);
            Put(_over, iconAt);
            Put(_play, new Rect(icon * 0.14f, icon * 0.26f, icon * 0.6f, icon * 0.6f));
            float g = icon * 0.42f;
            Put(_gear, new Rect(icon - g - icon * 0.03f, icon * 0.03f, g, g));
            float xs = Mathf.Round(box.height * 0.42f);
            Put(_x, new Rect(box.xMax - pad - xs, box.yMin + (box.height - xs) / 2f, xs, xs));
            if (_xText != null) _xText.fontSize = xs * 0.62f;
            TextArea = Rect.MinMaxRect(iconAt.xMax + pad * 1.4f, box.yMin + 4f, box.xMax - pad * 2f - xs, box.yMax - 4f);
            if (_text != null) Put(_text.rectTransform, TextArea);
        }

        private void LateUpdate()
        {
            Layout();
            bool show = _hover && ClientUi.Editing != this;
            if (_over != null && _over.gameObject.activeSelf != show) _over.gameObject.SetActive(show);
        }

        public void Draw()
        {
            if (Server == null) return;
            bool sel = ClientUi.Selected == Server.Address;
            if (_bg != null) { Color c = _bg.color; c.a = sel ? 1f : _hover ? 0.5f : 0.25f; _bg.color = c; }
            bool editing = ClientUi.Editing == this;
            if (_text != null) _text.enabled = !editing;
            if (_over != null) _over.gameObject.SetActive(_hover && !editing);
            if (_x != null) _x.gameObject.SetActive(!editing);
            string state, color;
            if (Server.Banned) { state = "Banned"; color = "#ff7766"; }
            else if (Server.State == "asking") { state = "Checking..."; color = "#c8d0d8"; }
            else if (Server.State == "online") { state = "Online  " + Server.Players + "/" + Server.Max + (Server.PingMs > 0 ? "  <color=#9ab>" + Server.PingMs + " ms</color>" : ""); color = "#7fe39a"; }
            else if (Server.State == "offline") { state = "Offline" + (Server.LastAnswer > DateTime.MinValue ? "  <color=#9ab>(up " + Ago(Server.LastAnswer) + ")</color>" : ""); color = "#ff8f80"; }
            else { state = ""; color = "#c8d0d8"; }
            string desc = Server.Desc.Length > 0 ? Server.Desc : Server.Address;
            if (desc.Length > 90) desc = desc.Substring(0, 87) + "...";
            if (_text != null)
                _text.text = "<b>" + Esc(ClientUi.Title(Server)) + "</b>   <color=" + color + ">" + state + "</color>\n<size=72%><color=#b8c8d8>" + Esc(desc) + "</color></size>";
            Texture2D icon = SavedServers.IconOf(Server);
            if (_dot != null && icon != null) { _dot.texture = icon; _dot.color = Server.Banned ? new Color(1f, 0.6f, 0.6f) : Color.white; return; }
            if (_dot != null) _dot.texture = Texture2D.whiteTexture;
            if (_dot != null) _dot.color = Server.Banned ? new Color(1f, 0.45f, 0.4f) : Server.State == "online" ? new Color(0.45f, 0.9f, 0.55f) : Server.State == "offline" ? new Color(1f, 0.5f, 0.45f, 0.8f) : new Color(0.7f, 0.75f, 0.8f, 0.6f);
        }

        private static string Esc(string s) { return (s ?? "").Replace("<", "‹").Replace(">", "›"); }

        private static string Ago(DateTime utc)
        {
            TimeSpan t = DateTime.UtcNow - utc;
            if (t.TotalMinutes < 60) return Math.Max(1, (int)t.TotalMinutes) + " min ago";
            if (t.TotalHours < 48) return (int)t.TotalHours + " h ago";
            return (int)t.TotalDays + " days ago";
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (ClientUi.Editing == this) return;
            bool dbl = Time.realtimeSinceStartup - _lastClick < 0.4f;
            _lastClick = Time.realtimeSinceStartup;
            ClientUi.Pick(this, dbl);
        }

        public void OnPointerEnter(PointerEventData e) { Hover(true); }
        public void OnPointerExit(PointerEventData e) { Hover(false); }

        /// <summary>The pointer on the row or off it (the buttons show in the same frame).</summary>
        public void Hover(bool on) { _hover = on; Draw(); }

        // ---- for the probe ----------------------------------------------------------------------

        public bool HoverShown { get { return _over != null && _over.gameObject.activeInHierarchy; } }
        public RectTransform PlayButton { get { return _play; } }
        public RectTransform SettingsButton { get { return _gear; } }
        public RectTransform RemoveButton { get { return _x; } }
        public RectTransform Icon { get { return _dot != null ? _dot.rectTransform : null; } }
        public RectTransform BoxRect { get { return _bg != null ? _bg.rectTransform : (RectTransform)transform; } }
        public void Press(RowButton.Kind k) { RowButton.Do(this, k); }

        internal void SetXHover(bool on)
        {
            if (_xBack != null) _xBack.color = new Color(1f, 1f, 1f, on ? 0.16f : 0f);
            if (_xText != null) _xText.color = new Color(1f, 1f, 1f, on ? 1f : 0.6f);
        }

        // ---- the little pictures, drawn once ------------------------------------------------------

        private static void MakeSprites()
        {
            if (_triangle != null) return;
            const int n = 96;
            _triangle = Picture(n, (x, y) =>
            {   // a play triangle pointing right
                Vector2 a = new Vector2(0.24f, 0.14f), b = new Vector2(0.24f, 0.86f), c = new Vector2(0.86f, 0.5f);
                Vector2 p = new Vector2(x, y);
                return Mathf.Min(Edge(a, b, p), Mathf.Min(Edge(b, c, p), Edge(c, a, p)));
            });
            _cog = Picture(n, (x, y) =>
            {   // a cog: a ring with eight teeth
                Vector2 p = new Vector2(x - 0.5f, y - 0.5f);
                float r = p.magnitude, ang = Mathf.Atan2(p.y, p.x);
                float tooth = Mathf.Clamp01((Mathf.Cos(ang * 8f) - 0.1f) * 3f);
                float outer = 0.34f + 0.12f * tooth;
                return Mathf.Min(outer - r, r - 0.15f);
            });
            _round = Picture(n, (x, y) => 0.5f - new Vector2(x - 0.5f, y - 0.5f).magnitude);
        }

        /// <summary>Distance from the line a-b, positive on its right (the inside of a triangle wound clockwise).</summary>
        private static float Edge(Vector2 a, Vector2 b, Vector2 p)
        {
            Vector2 d = (b - a).normalized;
            return (p.x - a.x) * d.y - (p.y - a.y) * d.x;
        }

        /// <summary>A white picture from a shape: `inside` gives the distance into it (in picture widths), smoothed over a pixel.</summary>
        private static Sprite Picture(int n, Func<float, float, float> inside)
        {
            Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = inside((x + 0.5f) / n, (y + 0.5f) / n) * n;
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(d + 0.5f) * 255f));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        }
    }

    /// <summary>Play, Settings and x on a saved server's row.</summary>
    internal class RowButton : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public enum Kind { Play, Settings, Remove }
        private ServerRow _row;
        private Kind _kind;
        private Graphic _look;

        public void Init(ServerRow row, Kind kind, Graphic look)
        {
            _row = row; _kind = kind; _look = look;
            if (_look != null) _look.color = new Color(1f, 1f, 1f, 0.85f);
        }

        public static void Do(ServerRow row, Kind kind)
        {
            if (row == null) return;
            if (kind == Kind.Play) ClientUi.PlayRow(row);
            else if (kind == Kind.Settings) ClientUi.EditRow(row);
            else ClientUi.Remove(row);
        }

        // a click here is ours: it doesn't also pick the row underneath
        public void OnPointerClick(PointerEventData e) { Do(_row, _kind); e.Use(); }

        public void OnPointerEnter(PointerEventData e)
        {
            if (_look != null) _look.color = _kind == Kind.Play ? new Color(0.55f, 1f, 0.65f, 1f) : Color.white;
            if (_kind == Kind.Remove && _row != null) _row.SetXHover(true);
        }

        public void OnPointerExit(PointerEventData e)
        {
            if (_look != null) _look.color = new Color(1f, 1f, 1f, 0.85f);
            if (_kind == Kind.Remove && _row != null) _row.SetXHover(false);
        }
    }
}
