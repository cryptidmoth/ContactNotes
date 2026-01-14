using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text.Json;
using System.Timers;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using BepisResoniteWrapper;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Md.FrooxEngine.ContactsDialog;
using MonoDetour;
using MonoDetour.HookGen;
using Plugin = ContactNotes.Plugin;

[assembly: MetadataUpdateHandler(typeof(Plugin))]

namespace ContactNotes;

[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS,
    PluginMetadata.REPOSITORY_URL)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID)]
[BepInDependency(BepisResoniteWrapper.PluginMetadata.GUID)]
[MonoDetourTargets(typeof(ContactsDialog))]
public class Plugin : BasePlugin
{
    internal new static ManualLogSource Log = null!;

    private static string? _selectedContactId;

    private static readonly SyncRef<TextField> ContactNotesTextField = new();
    private static UIBuilder? _contactNotesUi;

    private static readonly JsonSerializerOptions SerialiserOptions = new() { WriteIndented = true };
    private static string _oldNote = string.Empty;

    private static Dictionary<string, string> _notes = new();

    private static IObservable<IChangeable>? _debouncer;
    private static System.Timers.Timer saveTimer = new (1000);

    public override void Load()
    {
        Log = base.Log;
        //HarmonyInstance.PatchAll();
        ResoniteHooks.OnEngineReady += OnEngineReady;
        Config.Bind<dummy>("General", "ReloadIcon", default,
            new ConfigDescription("Click this button to reload the mod.", null, new Action(ProcessHotReload)));
        ModConfig.NotesFilePath = Config.Bind("General", "Notes Path", "notes.json",
            new ConfigDescription("Path for contact notes file "));

        MonoDetourManager.InvokeHookInitializers(Assembly.GetExecutingAssembly());

        Log.LogDebug($"Plugin {BepInExResoniteShim.PluginMetadata.GUID} is loaded!");
    }

    // MonoDetourManager.InvokeHookInitializers will
    // call methods marked with this attribute in types
    // which have the MonoDetourTargetsAttribute.
    [MonoDetourHookInitialize]
    private static void Init()
    {
        UpdateSelectedContactUI.Postfix(PostFixUpdateSelectedContactUi);

        if (!File.Exists(ModConfig.NotesFilePath.Value)) return;

        var notesFileString = File.ReadAllText(ModConfig.NotesFilePath.Value);

        _notes =
            JsonSerializer.Deserialize<Dictionary<string, string>>(notesFileString, SerialiserOptions) ??
            new Dictionary<string, string>();
    }

    // Function that runs every second to save the contact's note to a text file if it's different from the last time it was saved
    private static void SaveNoteTick()
    {
        if (_selectedContactId == null) return;
        var textFieldValue = ContactNotesTextField.Target.Text.Content.Value ?? string.Empty;

        if (string.IsNullOrEmpty(textFieldValue)) textFieldValue = string.Empty;

        if (textFieldValue == _oldNote) return;
        _notes[_selectedContactId] = textFieldValue;
        Log.LogDebug($"{_oldNote} != {textFieldValue}, saving...");
        _oldNote = textFieldValue;
        File.WriteAllText(ModConfig.NotesFilePath.Value,
            JsonSerializer.Serialize(_notes, SerialiserOptions));
    }


    /// <summary>
    ///     Updates the notes field for the selected contact in the contacts UI
    /// </summary>
    private static void PostFixUpdateSelectedContactUi(ContactsDialog self)
    {
        if (!self.World.IsUserspace()) return;
        Log.LogDebug(
            $"Updating selected contact {_selectedContactId}, value: {self.SelectedContact} " +
            $"\nSelected notes: {_notes.TryGetValue(_selectedContactId ?? string.Empty, out _)}");

        _selectedContactId = self.SelectedContactId ?? null;
        if (self.SelectedContact == null || !self.SelectedContact.IsValid)
        {
            if (self.SelectedContactId != null && _notes.TryGetValue(self.SelectedContactId, out _)) return;
            _selectedContactId = null;
            ContactNotesTextField?.Target?.Slot.Destroy();
            _contactNotesUi?.LayoutTarget?.DestroyChildren();
            _contactNotesUi?.LayoutTarget?.Destroy();
            _oldNote = string.Empty;
            SaveNoteTick();
            return;
        }

        _selectedContactId = self.SelectedContactId;

        Log.LogDebug("Updating Selected Contact UI");
        var sessionsListRect = self.sessionsUi.CurrentRect;
        if (sessionsListRect == null) self.sessionsUi.NestOut();

        // If the contact notes box hasn't already been initialized....
        if (ContactNotesTextField?.Target == null)
        {
            if (_contactNotesUi == null)
            {
                self.sessionsUi.HorizontalFooter(0f, out var footer, out var rect);
                _contactNotesUi = new UIBuilder(rect.Slot, footer.Slot);
                RadiantUI_Constants.SetupDefaultStyle(_contactNotesUi);
            }

            ContactNotesTextField.World = self.sessionsUi.World;
            ContactNotesTextField.Target = _contactNotesUi.TextField();
        }

        _notes.TryGetValue(_selectedContactId, out var value);
        _oldNote = value ?? string.Empty;
        ContactNotesTextField.Target.Text.Content.Value = value ?? string.Empty;

        saveTimer = new(1000);
        saveTimer.Elapsed += (sender, args) => SaveNoteTick();
        saveTimer.Start();
    }



    private void OnEngineReady()
    {
        // The Resonite engine is now fully initialized
        // Safe to access FrooxEngine classes and functionality
        Log.LogInfo("Engine is ready!");
        //HotReloadRegistry.RegisterReloadable(this);
    }

    public void ProcessHotReload()
    {
        DefaultMonoDetourManager.Instance.UndoHooks();
        DefaultMonoDetourManager.Instance.DisposeHooks();
        MonoDetourManager.InvokeHookInitializers(Assembly.GetExecutingAssembly());
        DefaultMonoDetourManager.Instance.ApplyHooks();
        var hooks = DefaultMonoDetourManager.Instance.Hooks;

        Log.LogMessage(
            $"{BepInExResoniteShim.PluginMetadata.NAME} is reloading {hooks.Count} hooks ({string.Join(", ", hooks.Select(n => n.Target.ToString()))})");
    }

    private struct ModConfig
    {
        public static ConfigEntry<string> NotesFilePath = null!;
    }
}