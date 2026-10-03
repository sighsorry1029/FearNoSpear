using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;

internal static class Program
{
    private static int _passed;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            System.Console.Error.WriteLine("Usage: FearNoSpear.Checks <Valheim directory> <built FearNoSpear.dll>");
            return 2;
        }
        string[] roots = { Path.Combine(args[0], "valheim_Data", "Managed"), Path.Combine(args[0], "BepInEx", "core"), Path.GetDirectoryName(Path.GetFullPath(args[1])) };
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            string file = roots.Select(root => Path.Combine(root, name)).FirstOrDefault(File.Exists);
            return file == null ? null : Assembly.LoadFrom(file);
        };
        try
        {
            using (var resolver = new DefaultAssemblyResolver())
            {
                foreach (string root in roots) resolver.AddSearchDirectory(root);
                var options = new ReaderParameters { AssemblyResolver = resolver };
                using (var game = ModuleDefinition.ReadModule(Path.Combine(roots[0], "assembly_valheim.dll"), options))
                using (var mod = ModuleDefinition.ReadModule(args[1], options))
                    CheckIL(game, mod);
            }
            Assembly builtMod = Assembly.LoadFrom(Path.GetFullPath(args[1]));
            CheckProtocol(builtMod);
            CheckNearestSelection(builtMod);
            CheckBeamHud(builtMod);
            System.Console.WriteLine($"PASS: {_passed} original-DLL static/serialization/placement checks. Unity/Harmony runtime gameplay was not executed.");
            return 0;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckIL(ModuleDefinition game, ModuleDefinition mod)
    {
        MethodDefinition pickup = Method(game, "Player", "AutoPickup");
        MethodDefinition spawn = Method(game, "Projectile", "SpawnOnHit");
        MethodDefinition drop = game.GetType("ItemDrop").Methods.Single(m => m.Name == "DropItem" && m.Parameters.Count == 4);
        MethodDefinition gate = Method(mod, "FearNoSpear.SpearAutoPickupPatch", "CanAutoPickup");
        MethodDefinition wrapper = Method(mod, "FearNoSpear.ProjectileSpawnItemPatch", "DropThrownItem");
        Expect(pickup.IsPrivate && spawn.IsPrivate, "patch targets are original private methods");
        Expect(pickup.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f && f.FullName == "System.Boolean ItemDrop::m_autoPickup") == 1,
            "exactly one original auto-pickup gate");
        Expect(Calls(spawn).Count(m => m.FullName == drop.FullName) == 1, "exactly one native spear drop call");
        Expect(gate.ReturnType.FullName == "System.Boolean" && gate.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "ItemDrop", "Player" }),
            "replacement auto-pickup stack signature");
        Expect(wrapper.ReturnType.FullName == drop.ReturnType.FullName &&
            wrapper.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(drop.Parameters.Select(p => p.ParameterType.FullName).Concat(new[] { "Projectile" })),
            "replacement spawn stack signature");
        Expect(Calls(wrapper).Count(m => m.FullName == drop.FullName) == 1, "wrapper creates exactly one native item");
        Expect(Calls(wrapper).Any(m => m.Name == "RecordSpawnedDrop"), "wrapper records the returned drop");
        Expect(!AllTypes(mod.Types).Where(t => t.Namespace == "FearNoSpear").SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(Calls).Any(m => m.Name == "FindBestMatchingNearbyDrop"), "nearby equivalence guessing removed");
        Expect(Calls(gate).Any(m => m.Name == "ReadFromDrop") && Calls(gate).Any(m => m.Name == "GetPlayerID"),
            "auto-pickup compares thrower metadata with player identity");
        Expect(game.GetType("ItemDrop").Fields.Single(f => f.Name == "s_instances").IsPrivate, "loaded-drop registry requires reflection");
        Expect(Calls(Method(mod, "FearNoSpear.SpearServerRegistry", "SelectBest")).Any(m => m.Name == "GetAllZDOIDsWithHash"),
            "background server lookup uses tagged ZDO candidates");
        Expect(!Calls(Method(mod, "FearNoSpear.SpearServerRegistry", "SelectBest")).Any(m => m.Name == "GetAllZDOsWithPrefabIterative"),
            "no per-prefab full-world search in polling path");

        MethodDefinition writeRecord = Method(mod, "FearNoSpear.SpearNetwork", "WriteRecord");
        Expect(writeRecord.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldfld).Select(i => ((FieldReference)i.Operand).Name)
            .SequenceEqual(new[] { "Key", "Position", "PrefabHash", "Variant" }), "response field order");
        Expect(Calls(Method(mod, "FearNoSpear.SpearNetwork", "ReadRecord")).Select(m => m.Name).Where(n => n.StartsWith("Read"))
            .SequenceEqual(new[] { "ReadString", "ReadVector3", "ReadInt", "ReadInt" }), "matching response reader order");
        Expect(Calls(Method(mod, "FearNoSpear.SpearNetwork", "RPC_SpearLocationResponse")).Any(m => m.Name == "ReceiveServerRecords"),
            "responses pass through the correlated request handler");

        Expect(mod.GetType("FearNoSpear.SpearChatCommand") == null && mod.GetType("FearNoSpear.SpearPinManager") == null &&
            mod.GetType("FearNoSpear.ChatSendInputPatch") == null, "archived chat and spear pin code is not compiled");
        Expect(!AllTypes(mod.Types).SelectMany(t => t.Methods).Any(m => m.Name == "CreateArrowMesh" || m.Name == "GetAnchor"),
            "character-relative arrow implementation is absent");
        MethodDefinition bypass = Method(mod, "FearNoSpear.TombStoneOwnerOnlyInteractPatch", "HasAdminDebugBypass");
        Expect(Calls(bypass).Any(m => m.Name == "LocalPlayerIsAdminOrHost") &&
            !Calls(bypass).Any(m => m.Name == "IsCheatsEnabled" || m.Name == "IsServer"),
            "dedicated-client admin bypass does not require host-only cheats");
        Expect(bypass.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "m_debugMode") &&
            bypass.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "m_localPlayer"),
            "admin bypass still requires local player and debug mode");
        Expect(!Calls(gate).Any(m => m.Name == "get_Value"), "thrower-only auto-pickup has no configuration bypass");

        TypeDefinition config = mod.GetType("FearNoSpear.FearNoSpearConfig");
        Expect(config.Fields.Where(f => f.FieldType.Name.StartsWith("ConfigEntry")).Select(f => f.Name).OrderBy(n => n)
            .SequenceEqual(new[] { "SpearIndicatorStyle", "MaxDisplayedSpears", "CleanDeathPins", "OwnerOnlyTombstones" }.OrderBy(n => n)),
            "configuration contains only the supported feature entries");
        var init = config.Methods.Single(m => m.IsConstructor).Body.Instructions;
        Expect(!init.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "Rescue"), "no Rescue config section is registered");
        foreach (string name in new[] { "SpearIndicatorStyle", "MaxDisplayedSpears", "CleanDeathPins", "OwnerOnlyTombstones" })
        {
            int index = init.ToList().FindIndex(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == name);
            Instruction description = init.Skip(index + 1).First(i => i.OpCode == OpCodes.Ldstr);
            bool local = name == "SpearIndicatorStyle" || name == "MaxDisplayedSpears" || name == "CleanDeathPins";
            Expect(description.Next.OpCode == (local ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1), name + " has the expected sync flag");
            if (name == "MaxDisplayedSpears")
                Expect(init[index + 1].OpCode == OpCodes.Ldc_I4_1 &&
                    description.Next.Next.OpCode == OpCodes.Ldc_I4_1 &&
                    description.Next.Next.Next.OpCode == OpCodes.Ldc_I4_5, "display count defaults to 1 with range 1 to 5");
            if (name == "SpearIndicatorStyle")
                Expect(init[index + 1].OpCode == OpCodes.Ldc_I4_0, "default style is BeamAndHud");
        }
        Expect(!Method(mod, "FearNoSpear.SpearNetwork", "RPC_RequestSpearLocation").Body.Instructions
            .Any(i => i.Operand is FieldReference f && f.DeclaringType.FullName == config.FullName),
            "server responses do not depend on server-local visual preferences");
        Expect(Method(mod, "FearNoSpear.SpearLocator", "Update").Body.Instructions
            .Any(i => i.Operand is FieldReference f && f.Name == "SpearIndicatorStyle"), "automatic polling observes Off");

        TypeDefinition tracker = mod.GetType("FearNoSpear.SpearSafetyTracker");
        Expect((float)tracker.Fields.Single(f => f.Name == "TtlRescueWindowSeconds").Constant == 1f &&
            (float)tracker.Fields.Single(f => f.Name == "LastKnownOwnerGraceSeconds").Constant == 2f,
            "rescue timing is fixed at 1 second with a 2-second last-owner window");
        Expect(!tracker.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Any(i => i.Operand is FieldReference f && f.Name == "Cfg"), "rescue policy has no config dependency");
        MethodDefinition ttlRescue = Method(mod, tracker.FullName, "TryTtlRescueAndDestroyIfNeeded");
        Expect(Calls(ttlRescue).Any(m => m.Name == "get_fixedDeltaTime") && Calls(ttlRescue).Any(m => m.Name == "Max") &&
            ttlRescue.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 1.5f) &&
            ttlRescue.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 1f),
            "fixed TTL rescue retains the physics-step safety margin");
        MethodDefinition ownerGate = Method(mod, tracker.FullName, "MayThisClientRescue");
        Expect(Calls(ownerGate).Any(m => m.Name == "IsValid") && Calls(ownerGate).Any(m => m.Name == "IsOwner"),
            "valid-view rescue still checks current network ownership");
        Expect(ownerGate.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f && f.Name == "_lastKnownOwner") &&
            ownerGate.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f && f.Name == "_lastOwnerStateTime") &&
            ownerGate.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 2f) &&
            Calls(ownerGate).Any(m => m.Name == "get_time"), "invalid-view fallback keeps the last-owner and freshness guards");
        MethodDefinition rescue = Method(mod, tracker.FullName, "TryRescue");
        Expect(new[] { "get_IsShuttingDown", "MayThisClientRescue", "TryClaimNetworkRescue", "TrySpawnOriginalItem", "ReleaseNetworkRescueClaim" }
            .All(name => Calls(rescue).Any(m => m.Name == name)), "shutdown, owner, claim, spawn, and failed-claim release paths remain");
        Expect(new[] { "_normalHit", "_rescueAttempted" }.All(name => rescue.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f && f.Name == name)),
            "normal-hit and one-attempt guards remain");

        List<string> inaccessible = new List<string>();
        foreach (TypeDefinition type in AllTypes(mod.Types).Where(t => t.Namespace == "FearNoSpear" || t.FullName.StartsWith("FearNoSpear.")))
        foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
        foreach (Instruction instruction in method.Body.Instructions)
        {
            if (instruction.Operand is MethodReference called && called.DeclaringType.Scope.Name == "assembly_valheim")
            {
                MethodDefinition resolved = called.Resolve();
                if (resolved != null && !resolved.IsPublic) inaccessible.Add(resolved.FullName);
            }
            if (instruction.Operand is FieldReference field && field.DeclaringType.Scope.Name == "assembly_valheim")
            {
                FieldDefinition resolved = field.Resolve();
                if (resolved != null && !resolved.IsPublic) inaccessible.Add(resolved.FullName);
            }
        }
        Expect(inaccessible.Count == 0, "no direct non-public game member access: " + string.Join(", ", inaccessible.Distinct()));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckProtocol(Assembly mod)
    {
        Type recordType = mod.GetType("FearNoSpear.SpearLocationRecord", true);
        Type network = mod.GetType("FearNoSpear.SpearNetwork", true);
        object record = Activator.CreateInstance(recordType, true);
        Set(record, "Key", "drop:123:456");
        Set(record, "Position", new Vector3(-123f, 45f, 6789f));
        Set(record, "PrefabHash", 123456);
        Set(record, "Variant", 2);
        ZPackage package = new ZPackage();
        network.GetMethod("WriteHeader", Fields).Invoke(null, new object[] { package });
        network.GetMethod("WriteRecord", Fields).Invoke(null, new[] { package, record });
        ZPackage reader = new ZPackage(package.GetArray());
        Expect(reader.ReadInt() == 6, "protocol bumped to 6");
        object copy = network.GetMethod("ReadRecord", Fields).Invoke(null, new object[] { reader });
        Expect((string)Get(copy, "Key") == "drop:123:456", "drop identity roundtrip");
        Vector3 position = (Vector3)Get(copy, "Position");
        Expect(position.x == -123f && position.y == 45f && position.z == 6789f, "position roundtrip");
        Expect((int)Get(copy, "PrefabHash") == 123456 && (int)Get(copy, "Variant") == 2, "icon identity roundtrip");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Expect(!(bool)network.GetMethod("IsFinite", Fields).Invoke(null, new object[] { new Vector3(invalid, 0f, 0f) }), "invalid position rejected");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckNearestSelection(Assembly mod)
    {
        Type locator = mod.GetType("FearNoSpear.SpearLocator", true);
        Type targetType = mod.GetType("FearNoSpear.SpearLocator+Target", true);
        Type listType = typeof(List<>).MakeGenericType(targetType);
        MethodInfo add = locator.GetMethod("AddNearest", Fields);
        object Target(string key, Vector3 position) => Activator.CreateInstance(targetType, Fields, null,
            new object[] { key, position, null }, null);

        foreach (int limit in Enumerable.Range(1, 5))
        {
            var targets = (System.Collections.IList)Activator.CreateInstance(listType);
            foreach (int distance in new[] { 7, 3, 6, 1, 2, 5, 4 })
                add.Invoke(null, new object[] { targets, Target("drop:" + distance, new Vector3(distance, 0f, 0f)), Vector3.zero, limit });
            Expect(targets.Count == limit, "nearest selection respects limit " + limit);
            Expect(targets.Cast<object>().Select(t => (string)Get(t, "Key"))
                .SequenceEqual(Enumerable.Range(1, limit).Select(i => "drop:" + i)), "nearest selection order at limit " + limit);
            add.Invoke(null, new object[] { targets, Target("drop:1", Vector3.zero), Vector3.zero, limit });
            Expect(targets.Count == limit && ((Vector3)Get(targets[0], "Position")).x == 1f,
                "duplicate server record cannot replace the first loaded record at limit " + limit);
            targets.RemoveAt(0);
            add.Invoke(null, new object[] { targets, Target("drop:new", Vector3.zero), Vector3.zero, limit });
            Expect((string)Get(targets[0], "Key") == "drop:new", "new target replaces recovered spear at limit " + limit);
        }
        var tied = (System.Collections.IList)Activator.CreateInstance(listType);
        foreach (string key in new[] { "drop:b", "drop:a" })
            add.Invoke(null, new object[] { tied, Target(key, Vector3.one), Vector3.zero, 5 });
        Expect((string)Get(tied[0], "Key") == "drop:a", "equal-distance targets have deterministic identity order");
        add.Invoke(null, new object[] { tied, Target("", Vector3.zero), Vector3.zero, 5 });
        Expect(tied.Count == 2, "empty identities are ignored");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckBeamHud(Assembly mod)
    {
        Type indicator = mod.GetType("FearNoSpear.SpearIndicator", true);
        Type style = mod.GetType("FearNoSpear.FearNoSpearPlugin+IndicatorStyle", true);
        Expect(Enum.GetNames(style).SequenceEqual(new[] { "BeamAndHud", "Beam", "Hud", "Off" }), "only the four current display styles are available");
        Expect((float)indicator.GetField("BeamHeight", Fields).GetRawConstantValue() == 500f, "beam height is 500 meters");
        MethodInfo width = indicator.GetMethod("GetBeamWidth", Fields);
        Expect((float)width.Invoke(null, new object[] { 0f }) == 0.1f, "near beam has a fixed minimum width of 0.1 meters");
        Expect(Math.Abs((float)width.Invoke(null, new object[] { 500f }) - 0.7f) < 0.0001f, "beam growth rate remains unchanged at 500 meters");
        Expect((float)width.Invoke(null, new object[] { 100000f }) == 1f, "far beam has a fixed maximum width of 1 meter");
        float previousWidth = 0.1f;
        foreach (float distance in new[] { 0f, 50f, 71f, 72f, 150f, 500f, 714f, 715f, 1000f, 100000f })
        {
            float actual = (float)width.Invoke(null, new object[] { distance });
            Expect(actual >= 0.1f && actual <= 1f && actual >= previousWidth,
                $"fixed beam width stays bounded and monotonic at {distance} meters");
            previousWidth = actual;
        }

        MethodInfo placement = indicator.GetMethod("GetHudPlacement", Fields);
        MethodInfo hudScale = indicator.GetMethod("GetHudScale", Fields);
        (bool Edge, Vector2 Position, Vector2 Direction) Place(Vector3 point, Vector2 behind, Rect viewport, Rect safe)
        {
            float scale = (float)hudScale.Invoke(null, new object[] { safe, 1 });
            object[] args = { point, behind, viewport, safe, scale, Vector2.zero, Vector2.zero };
            bool edge = (bool)placement.Invoke(null, args);
            return (edge, (Vector2)args[5], (Vector2)args[6]);
        }

        Rect[] viewports = { new Rect(0f, 0f, 1920f, 1080f), new Rect(0f, 0f, 2560f, 1080f),
            new Rect(0f, 0f, 800f, 1200f), new Rect(0f, 0f, 320f, 200f), new Rect(80f, 50f, 1120f, 620f) };
        Vector3[] targets = { new Vector3(-1f, 0.5f, 10f), new Vector3(2f, 0.5f, 10f),
            new Vector3(0.5f, -1f, 10f), new Vector3(0.5f, 2f, 10f), new Vector3(-1f, 2f, 10f),
            new Vector3(2f, -1f, 10f), new Vector3(0.5f, 0.5f, -10f), new Vector3(0.5f, 0.5f, 0f) };
        foreach (Rect viewport in viewports)
        {
            Rect safe = Rect.MinMaxRect(viewport.xMin + 8f, viewport.yMin + 8f, viewport.xMax - 8f, viewport.yMax - 8f);
            float scale = (float)hudScale.Invoke(null, new object[] { safe, 1 });
            foreach (Vector3 target in targets)
            {
                var result = Place(target, Vector2.zero, viewport, safe);
                bool fits = result.Position.x - 78f * scale >= safe.xMin && result.Position.x + 78f * scale <= safe.xMax &&
                    result.Position.y - 22f * scale >= safe.yMin && result.Position.y + 60f * scale <= safe.yMax;
                Expect(result.Edge && fits && !float.IsNaN(result.Position.sqrMagnitude),
                    $"edge HUD fits {viewport.width}x{viewport.height}, target=({target.x},{target.y},{target.z})");
            }
            var near = Place(new Vector3(0.5f, 0.5f, 10f), Vector2.zero, viewport, safe);
            var far = Place(new Vector3(0.5f, 0.5f, 100000f), Vector2.zero, viewport, safe);
            Expect(!near.Edge && !far.Edge && (near.Position - far.Position).sqrMagnitude < 0.0001f,
                $"on-screen HUD survives far clip distance at {viewport.width}x{viewport.height}");
            var left = Place(new Vector3(0.5f, 0.5f, -10f), Vector2.left, viewport, safe);
            var right = Place(new Vector3(0.5f, 0.5f, -10f), Vector2.right, viewport, safe);
            Expect(left.Position.x < safe.center.x && left.Direction.x < 0f && right.Position.x > safe.center.x && right.Direction.x > 0f,
                $"behind-camera direction is not mirrored at {viewport.width}x{viewport.height}");
        }

        MethodInfo arrange = indicator.GetMethod("ArrangeHud", Fields);
        foreach (Rect viewport in viewports)
        foreach (int count in Enumerable.Range(1, 5))
        foreach (Vector2 desired in new[] { viewport.center, viewport.min, viewport.max })
        {
            float scale = (float)hudScale.Invoke(null, new object[] { viewport, count });
            Vector2 start = new Vector2(Mathf.Clamp(desired.x, viewport.xMin + 100f * scale, viewport.xMax - 100f * scale),
                Mathf.Clamp(desired.y, viewport.yMin + 68f * scale, viewport.yMax - 68f * scale));
            List<Vector2> placed = Enumerable.Repeat(start, count).ToList();
            arrange.Invoke(null, new object[] { viewport, scale, placed });
            for (int i = 0; i < count; ++i)
            {
                Vector2 point = placed[i];
                Expect(placed.Take(i).All(p => Mathf.Abs(point.x - p.x) >= 160f * scale ||
                    Mathf.Abs(point.y - p.y) >= 90f * scale - 0.02f), $"HUD {i + 1}/{count} does not overlap at {viewport.width}x{viewport.height}");
                Expect(point.x - 78f * scale >= viewport.xMin && point.x + 78f * scale <= viewport.xMax &&
                    point.y - 22f * scale >= viewport.yMin && point.y + 60f * scale <= viewport.yMax,
                    "separated HUD remains in the safe viewport");
            }
        }

        System.Random random = new System.Random(42);
        foreach (Rect viewport in viewports)
        {
            bool fits = true;
            float scale = (float)hudScale.Invoke(null, new object[] { viewport, 5 });
            for (int sample = 0; sample < 1000; ++sample)
            {
                List<Vector2> placed = new List<Vector2>();
                for (int i = 0; i < 5; ++i)
                {
                    Vector2 desired = new Vector2(
                        Mathf.Lerp(viewport.xMin + 100f * scale, viewport.xMax - 100f * scale, (float)random.NextDouble()),
                        Mathf.Lerp(viewport.yMin + 68f * scale, viewport.yMax - 68f * scale, (float)random.NextDouble()));
                    placed.Add(desired);
                }
                arrange.Invoke(null, new object[] { viewport, scale, placed });
                for (int i = 0; i < placed.Count; ++i)
                {
                    Vector2 point = placed[i];
                    fits &= placed.Take(i).All(p => Mathf.Abs(point.x - p.x) >= 160f * scale ||
                        Mathf.Abs(point.y - p.y) >= 90f * scale - 0.02f);
                    fits &= point.x - 78f * scale >= viewport.xMin && point.x + 78f * scale <= viewport.xMax &&
                        point.y - 22f * scale >= viewport.yMin && point.y + 60f * scale <= viewport.yMax;
                }
            }
            Expect(fits, $"mixed-position HUD sets do not overlap at {viewport.width}x{viewport.height}");
        }
    }

    private static MethodDefinition Method(ModuleDefinition module, string type, string name)
        => module.GetType(type).Methods.Single(m => m.Name == name);
    private static IEnumerable<MethodReference> Calls(MethodDefinition method)
        => method.Body.Instructions.Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt).Select(i => (MethodReference)i.Operand);
    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }
    private static object Get(object target, string name) => target.GetType().GetField(name, Fields).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Fields).SetValue(target, value);
    private static void Expect(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        ++_passed;
        System.Console.WriteLine("PASS: " + label);
    }
}
