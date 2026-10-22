using System.Collections;
using System.IO;
using System.Text;
using Freehand.Customization;
using Freehand.Paint;
using UnityEngine;

// Freehand play-mode validation suite, run headless by tools/headless.ps1 (HeadlessRunner.RunTests).
// V0.14 workshop: a full shelf never replaces a saved can, delete needs a second press, Clear All never reaches the can.
// The Editor's own saved cans are moved aside for the test and put back after it.
public static partial class FreehandPlayTests
{
    public static IEnumerator Workshop(StringBuilder log)
    {
        var folder = CanSkinStore.Folder;
        var aside = folder + "_testbackup";
        if (Directory.Exists(aside))
            Directory.Delete(aside, true);
        if (Directory.Exists(folder))
            Directory.Move(folder, aside);

        var skin = GameObject.Find("SprayCan").GetComponent<CanSkin>();
        var workshop = Object.FindAnyObjectByType<CanWorkshop>();
        var shelf = Object.FindAnyObjectByType<CanShelf>();
        var canvas = skin.Canvas;

        // The skin read its (now moved) index at startup: start it from an empty shelf.
        typeof(CanSkin).GetField("index", Any).SetValue(skin, CanSkinStore.LoadIndex());
        skin.Load(null);
        shelf.Rebuild();
        yield return 0.2f;

        Color32[] Painted(int seed)
        {
            var pixels = (Color32[])canvas.BasePixels.Clone();
            var size = canvas.TextureSize;
            for (var y = size / 3; y < size / 3 + 20 + seed * 3; y++)
                for (var x = size / 4; x < size / 4 + 30 + seed * 5; x++)
                    pixels[y * size + x] = new Color32((byte)(40 * seed), 200, 90, 255);
            return pixels;
        }

        // Six saved cans.
        for (var i = 0; i < CanSkin.MaxSaved; i++)
        {
            skin.Load(null);
            canvas.ReplacePaint(Painted(i + 1));
            yield return null;
            workshop.PressSave();
            yield return 0.1f;
        }

        var files = Directory.GetFiles(folder, "*.fhpaint").Length;
        Check(log, skin.Index.skins.Count == CanSkin.MaxSaved && files == CanSkin.MaxSaved, "six cans saved", $"{skin.Index.skins.Count} entries, {files} files");

        // A seventh: refused, nothing replaced.
        var names = string.Join(",", skin.Index.skins.ConvertAll(s => s.name));
        skin.Load(null);
        canvas.ReplacePaint(Painted(7));
        yield return null;
        workshop.PressSave();
        yield return 0.1f;
        Check(log, skin.Index.skins.Count == CanSkin.MaxSaved && Directory.GetFiles(folder, "*.fhpaint").Length == CanSkin.MaxSaved && string.Join(",", skin.Index.skins.ConvertAll(s => s.name)) == names,
            "full shelf: a 7th save is refused and no saved can is replaced", $"plate '{workshop.PlateState}'");
        Check(log, workshop.PlateState.StartsWith("CHEIA"), "...and the plate says the shelf is full", workshop.PlateState);
        yield return 2.7f;
        Check(log, workshop.PlateState.Contains("CHEIA") || workshop.PlateState == "NOVA", "after the notice: the plate shows the unsaved can (and that the shelf is full)", workshop.PlateState);

        // Delete: the first press only asks.
        var target = skin.Index.skins[2];
        skin.Load(target);
        yield return 0.1f;
        var hashBefore = canvas.PixelHash();
        workshop.PressDelete();
        yield return 0.1f;
        Check(log, workshop.PlateState == "CONFIRME" && skin.Index.skins.Count == CanSkin.MaxSaved, "APAGAR once: asks for confirmation, deletes nothing", workshop.PlateState);

        // Too late a second press: asks again, still nothing deleted.
        yield return 3.3f;
        Check(log, workshop.PlateState != "CONFIRME", "the question times out", workshop.PlateState);
        workshop.PressDelete();
        yield return 0.1f;
        Check(log, workshop.PlateState == "CONFIRME" && skin.Index.skins.Count == CanSkin.MaxSaved, "a press after the time-out asks again");

        // Second press in time: gone, file and all; the paint stays on the can, unsaved.
        workshop.PressDelete();
        yield return 0.2f;
        var fileGone = !File.Exists(Path.Combine(folder, target.paintFile));
        Check(log, skin.Index.skins.Count == CanSkin.MaxSaved - 1 && skin.Index.Find(target.id) == null && fileGone, "APAGAR twice: that can is deleted (entry and file)", $"{skin.Index.skins.Count} left");
        Check(log, canvas.PixelHash() == hashBefore && skin.Active == null && skin.HasUnsavedPaint, "the paint stays on the can as unsaved (nothing vanishes from view)");
        Check(log, workshop.PlateState.EndsWith("APAGADA"), "the plate says it was deleted", workshop.PlateState);

        // The shelf follows.
        var visible = 0;
        foreach (Transform slot in shelf.transform)
            if (slot.gameObject.activeSelf && slot.name.StartsWith("Slot")) visible++;
        log.AppendLine($"  shelf slots visible: {visible} (plain can + {skin.Index.skins.Count} saved)");

        // Room again: the new can saves.
        workshop.PressSave();
        yield return 0.1f;
        Check(log, skin.Index.skins.Count == CanSkin.MaxSaved && skin.Active != null, "with a place free, saving works again");

        // The plain can cannot be deleted.
        skin.Load(null);
        yield return 0.1f;
        workshop.PressDelete();
        yield return 0.1f;
        Check(log, workshop.PlateState == "PADRAO FICA" && skin.Index.skins.Count == CanSkin.MaxSaved, "APAGAR on the plain can: nothing to delete, it says so");

        // Clear All (the walls) never reaches the can.
        skin.Load(skin.Index.skins[0]);
        yield return 0.1f;
        var canHash = canvas.PixelHash();
        PaintManager.ClearAll();
        Check(log, canvas.PixelHash() == canHash, "Clear All leaves the can's paint alone");

        // A new session reads the shelf back.
        var reread = CanSkinStore.LoadIndex();
        Check(log, reread.skins.Count == CanSkin.MaxSaved && reread.activeSkinId == skin.Active.id, "the shelf and the can in use read back from disk");

        // Put the Editor's own cans back.
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        if (Directory.Exists(aside))
            Directory.Move(aside, folder);
        yield return 0.1f;
    }
}
