using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;
using CodeInstruction = HarmonyLib.CodeInstruction;
using EmitOpCodes = System.Reflection.Emit.OpCodes;

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
            CheckAutoPickupComposition(builtMod);
            CheckProtocol(builtMod);
            CheckSpearClassification(builtMod);
            CheckNearestSelection(builtMod);
            CheckTombstones(builtMod);
            CheckCandidateCache(builtMod);
            CheckSelectionCache(builtMod);
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
        Expect(gate.ReturnType.FullName == "System.Boolean" && gate.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "ItemDrop", "System.Boolean", "Player" }),
            "composable auto-pickup stack signature retains the incoming permission");
        Expect(!gate.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "m_autoPickup"),
            "auto-pickup filter does not replace a preceding restriction by rereading the native flag");
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
        MethodDefinition candidateRefresh = Method(mod, "FearNoSpear.SpearServerRegistry", "RefreshCandidates");
        Expect(Calls(candidateRefresh).Any(m => m.Name == "GetZDO") && Calls(candidateRefresh).Any(m => m.Name == "GetLong"),
            "shared candidate refresh groups existing ZDOs by live player identity");
        Expect((float)mod.GetType("FearNoSpear.SpearServerRegistry").Fields.Single(f => f.Name == "CandidateCacheSeconds").Constant == 5f,
            "world candidate scans use a five-second shared cache");
        Expect(!Calls(Method(mod, "FearNoSpear.SpearServerRegistry", "SelectBest")).Any(m => m.Name == "GetAllZDOsWithPrefabIterative"),
            "no per-prefab full-world search in polling path");

        MethodDefinition writeRecord = Method(mod, "FearNoSpear.SpearNetwork", "WriteRecord");
        Expect(writeRecord.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldfld).Select(i => ((FieldReference)i.Operand).Name)
            .SequenceEqual(new[] { "Key", "Position", "PrefabHash", "Variant", "Kind", "CreatedTicks" }), "response field order");
        Expect(Calls(Method(mod, "FearNoSpear.SpearNetwork", "ReadRecord")).Select(m => m.Name).Where(n => n.StartsWith("Read"))
            .SequenceEqual(new[] { "ReadString", "ReadVector3", "ReadInt", "ReadInt", "ReadInt", "ReadLong" }), "matching response reader order");
        Expect(Calls(Method(mod, "FearNoSpear.SpearNetwork", "RPC_SpearLocationResponse")).Any(m => m.Name == "ReceiveServerRecords"),
            "responses pass through the correlated request handler");

        Expect(mod.GetType("FearNoSpear.SpearChatCommand") == null && mod.GetType("FearNoSpear.SpearPinManager") == null &&
            mod.GetType("FearNoSpear.ChatSendInputPatch") == null, "archived chat and spear pin code is not compiled");
        Expect(!AllTypes(mod.Types).SelectMany(t => t.Methods).Any(m => m.Name == "CreateArrowMesh" || m.Name == "GetAnchor"),
            "character-relative arrow implementation is absent");
        TypeDefinition indicatorType = mod.GetType("FearNoSpear.SpearIndicator");
        MethodDefinition markerConstructor = indicatorType.NestedTypes.Single(t => t.Name == "Marker").Methods.Single(m => m.IsConstructor);
        Expect(Calls(markerConstructor).Any(m => m.Name == "set_sharedMaterial") &&
            !Calls(markerConstructor).Any(m => m.Name == "set_material" || m.DeclaringType.FullName == "UnityEngine.Material"),
            "pooled beams share a material instead of allocating one per target");
        Expect(!Calls(Method(mod, indicatorType.FullName, "Update")).Any(m => m.DeclaringType.FullName == "UnityEngine.Material"),
            "per-frame indicator update does not create or modify materials");
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
            .SequenceEqual(new[] { "SpearIndicatorStyle", "TombstoneIndicatorStyle", "MaxDisplayedSpears", "MaxDisplayedTombstones", "CleanDeathPins", "OwnerOnlyTombstones" }.OrderBy(n => n)),
            "configuration contains only the supported feature entries");
        var init = config.Methods.Single(m => m.IsConstructor).Body.Instructions;
        Expect(!init.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "Rescue"), "no Rescue config section is registered");
        foreach (string name in new[] { "SpearIndicatorStyle", "TombstoneIndicatorStyle", "MaxDisplayedSpears", "MaxDisplayedTombstones", "CleanDeathPins", "OwnerOnlyTombstones" })
        {
            int index = init.ToList().FindIndex(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == name);
            Instruction description = init.Skip(index + 1).First(i => i.OpCode == OpCodes.Ldstr);
            bool local = name != "OwnerOnlyTombstones";
            Expect(description.Next.OpCode == (local ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1), name + " has the expected sync flag");
            if (name == "MaxDisplayedSpears" || name == "MaxDisplayedTombstones")
                Expect(init[index + 1].OpCode == OpCodes.Ldc_I4_1 &&
                    description.Next.Next.OpCode == OpCodes.Ldc_I4_1 &&
                    description.Next.Next.Next.OpCode == OpCodes.Ldc_I4_5, name + " defaults to 1 with range 1 to 5");
            if (name == "SpearIndicatorStyle" || name == "TombstoneIndicatorStyle")
                Expect(init[index + 1].OpCode == OpCodes.Ldc_I4_0, "default style is BeamAndHud");
        }
        Expect(!Method(mod, "FearNoSpear.SpearNetwork", "RPC_RequestSpearLocation").Body.Instructions
            .Any(i => i.Operand is FieldReference f && f.DeclaringType.FullName == config.FullName),
            "server responses do not depend on server-local visual preferences");
        Expect(Method(mod, "FearNoSpear.SpearLocator", "Update").Body.Instructions
            .Any(i => i.Operand is FieldReference f && f.Name == "SpearIndicatorStyle"), "automatic polling observes Off");
        Expect(Method(mod, "FearNoSpear.SpearLocator", "Update").Body.Instructions
            .Any(i => i.Operand is FieldReference f && f.Name == "TombstoneIndicatorStyle"), "tombstone-only polling is supported");
        Expect(Method(mod, "FearNoSpear.SpearLocator", "GetTargets").Body.Instructions
            .Any(i => i.Operand is FieldReference f && f.Name == "MaxDisplayedTombstones"), "local selection applies the personal tombstone count");
        MethodDefinition detector = Method(mod, "FearNoSpear.SpearProjectileDetector", "IsTrackedSpearProjectile");
        Expect(Calls(detector).First().Name == "IsRecoverableProjectile" && detector.Body.Instructions
            .Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "SecondaryAttacks_CopiedThrowProjectile"),
            "SecondaryAttacks marker support retains the recoverable-item gate");
        MethodDefinition recoverable = Method(mod, "FearNoSpear.SpearProjectileDetector", "IsRecoverableProjectile");
        Expect(new[] { "F_respawnItemOnHit", "F_spawnItem" }.All(name => recoverable.Body.Instructions
            .Any(i => i.Operand is FieldReference f && f.Name == name)), "suppressed virtual/caught projectiles cannot qualify for rescue");
        MethodDefinition setupPatch = Method(mod, "FearNoSpear.ProjectileSetupPatch", "Postfix");
        Expect(setupPatch.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyAfter" &&
            ((CustomAttributeArgument[])a.ConstructorArguments[0].Value).Any(v => (string)v.Value == "sighsorry.SecondaryAttacks")),
            "tracking setup runs after SecondaryAttacks metadata setup");
        foreach (MethodDefinition method in new[] { Method(mod, "FearNoSpear.SpearLocator", "RefreshLoadedDrops"),
            Method(mod, "FearNoSpear.SpearServerRegistry", "SelectBest"), Method(mod, "FearNoSpear.HumanoidPickupPatch", "Prefix") })
            Expect(!Calls(method).Any(m => m.Name == "IsSpearItem"), "tagged non-spear drops accepted in " + method.FullName);
        Expect(Calls(Method(mod, "FearNoSpear.HumanoidPickupPatch", "Prefix")).Any(m => m.Name == "ReadFromDrop"),
            "pickup invalidation requires a tagged exact drop");
        MethodDefinition graveAwake = Method(game, "TombStone", "Awake");
        Expect(graveAwake.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "s_timeOfDeath") &&
            Calls(graveAwake).Any(m => m.Name == "GetTime"), "original tombstone creation records world time");
        Expect(Method(game, "TombStone", "Setup").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "s_owner"),
            "original tombstone setup records character owner ID");
        MethodDefinition registry = Method(mod, "FearNoSpear.SpearServerRegistry", "SelectBest");
        Expect(Calls(registry).Count(m => m.Name == "TryGetValue" && m.DeclaringType.Name.StartsWith("Dictionary")) == 2,
            "requests select only each player's cached candidate lists");
        Expect(Calls(registry).Count(m => m.Name == "GetAllZDOIDsWithHash") == 2 &&
            Calls(registry).Count(m => m.Name == "RefreshCandidates") == 2,
            "each category has one native scan and one grouping call at its cache refresh");
        Expect(Calls(registry).Any(m => m.Name == "ReadFromZdo") && Calls(registry).Any(m => m.Name == "GetLong"),
            "cached candidates still revalidate live ownership before a response");
        Expect(new[] { "s_owner", "s_timeOfDeath" }.All(name => registry.Body.Instructions
            .Any(i => i.Operand is FieldReference f && f.Name == name)), "server uses native tombstone owner and creation time");
        Expect(Calls(registry).Any(m => m.Name == "OrderByDescending"), "server returns newest tombstones before truncating");
        Expect(Calls(Method(mod, "FearNoSpear.SpearLocator", "HideEmptyTombstone")).Any(m => m.Name == "IsEmpty"),
            "partial tombstone recovery does not suppress its marker");
        Expect(!Calls(Method(mod, "FearNoSpear.SpearLocator", "HideEmptyTombstone")).Any(m => m.DeclaringType.Name == "DeathPinCleaner") &&
            !Calls(Method(mod, "FearNoSpear.SpearLocator", "GetTargets")).Any(m => m.Name == "GetPins"),
            "tombstone indicators do not depend on death-pin cleanup or saved map pins");
        foreach (string hook in new[] { "TombStoneTakeAllDeathPinPatch", "TombStoneUpdateDespawnDeathPinPatch" })
            Expect(Calls(mod.GetType("FearNoSpear." + hook).Methods.Single(m => m.Name == "Postfix" || m.Name == "Prefix"))
                .Any(m => m.Name == "HideEmptyTombstone"), "tombstone recovery hook updates markers: " + hook);

        MethodDefinition getTargets = Method(mod, "FearNoSpear.SpearLocator", "GetTargets");
        Expect(!Calls(getTargets).Any(m => m.Name == "GetPrefab" || m.Name == "GetComponent" || m.Name == "GetIcon"),
            "per-frame targets reuse icons and do not look up prefabs or components");
        Expect(Calls(getTargets).Count(m => m.Name == "GetEnumerator") == 1 &&
            getTargets.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "SelectedTargets"),
            "per-frame updates iterate only the bounded selected target list");
        Expect(new[] { "_selectionDirty", "_selectedWeaponLimit", "_selectedTombstoneLimit" }.All(name =>
            getTargets.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == name)),
            "selection observes invalidation and both personal count/style limits");
        foreach (string methodName in new[] { "Clear", "Update", "RefreshLoadedDrops", "ForgetRecoveredTarget" })
        {
            MethodDefinition method = Method(mod, "FearNoSpear.SpearLocator", methodName);
            Expect(method.Body.Instructions.Any(i => i.OpCode == OpCodes.Stsfld &&
                i.Operand is FieldReference f && f.Name == "_selectionDirty" && i.Previous.OpCode == OpCodes.Ldc_I4_1),
                methodName + " invalidates cached selection");
        }
        Expect(Calls(Method(mod, "FearNoSpear.SpearLocator", "ReceiveServerRecords")).Any(m => m.Name == "RefreshLoadedDrops"),
            "accepted server responses immediately refresh candidates and invalidate selection");

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

        CheckRescueSpawnContracts(game, mod);

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
    private static void CheckAutoPickupComposition(Assembly mod)
    {
        Type patch = mod.GetType("FearNoSpear.SpearAutoPickupPatch", true);
        MethodInfo transpiler = patch.GetMethod("Transpiler", Fields);
        MethodInfo gate = patch.GetMethod("CanAutoPickup", Fields);
        MethodInfo otherGate = typeof(Program).GetMethod(nameof(OtherAutoPickupFilter), Fields);
        FieldInfo field = typeof(ItemDrop).GetField("m_autoPickup");
        List<CodeInstruction> Original() => new List<CodeInstruction>
        {
            new CodeInstruction(EmitOpCodes.Ldarg_1),
            new CodeInstruction(EmitOpCodes.Ldfld, field),
            new CodeInstruction(EmitOpCodes.Ret)
        };
        List<CodeInstruction> Apply(IEnumerable<CodeInstruction> code)
            => ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { code })).ToList();
        bool IsField(CodeInstruction i) => i.opcode == EmitOpCodes.Ldfld && Equals(i.operand, field);

        // The matcher/insertion shape from QuickStackStore 1.4.15; no external mod DLL is required.
        List<CodeInstruction> Other(IEnumerable<CodeInstruction> code)
            => new HarmonyLib.CodeMatcher(code).MatchForward(false, new HarmonyLib.CodeMatch(IsField))
                .InsertAndAdvance(new CodeInstruction(EmitOpCodes.Dup)).Advance(1)
                .InsertAndAdvance(new CodeInstruction(EmitOpCodes.Ldarg_0))
                .InsertAndAdvance(new CodeInstruction(EmitOpCodes.Call, otherGate)).Instructions();

        List<CodeInstruction> original = Original();
        CodeInstruction nativeRead = original[1];
        var generator = new System.Reflection.Emit.DynamicMethod("UnusedLabels", typeof(void), Type.EmptyTypes).GetILGenerator();
        var label = generator.DefineLabel();
        var block = new HarmonyLib.ExceptionBlock(HarmonyLib.ExceptionBlockType.BeginExceptionBlock);
        nativeRead.labels.Add(label);
        nativeRead.blocks.Add(block);
        List<CodeInstruction> alone = Apply(original);
        Expect(alone.Count(IsField) == 1 && alone.Contains(nativeRead), "native auto-pickup read survives transpilation unchanged");
        Expect(alone[1].opcode == EmitOpCodes.Dup && alone[1].labels.Contains(label) && alone[1].blocks.Contains(block) &&
            nativeRead.labels.Count == 0 && nativeRead.blocks.Count == 0,
            "branches and exception-block entry reach the inserted duplicate before the native read");
        Expect(!(bool)gate.Invoke(null, new object[] { null, false, null }),
            "a preceding denial short-circuits before inspecting the drop or player");

        foreach (bool fearFirst in new[] { true, false })
        {
            List<CodeInstruction> combined = fearFirst ? Other(Apply(Original())) : Apply(Other(Original()));
            Expect(combined.Count(IsField) == 1 && combined.Last().opcode == EmitOpCodes.Ret &&
                combined.Count(i => Equals(i.operand, gate)) == 1 && combined.Count(i => Equals(i.operand, otherGate)) == 1,
                "both filters stay in the original gate, not after ret; FearNoSpear first=" + fearFirst);
            foreach (bool native in new[] { false, true })
            foreach (bool thrower in new[] { false, true })
            foreach (bool trash in new[] { false, true })
            {
                // Interpret only this gate's stack. Permissions are independent inputs, not Unity fixtures.
                object dropToken = new object(), playerToken = new object();
                Stack<object> stack = new Stack<object>();
                foreach (CodeInstruction instruction in combined)
                {
                    if (instruction.opcode == EmitOpCodes.Ldarg_1) stack.Push(dropToken);
                    else if (instruction.opcode == EmitOpCodes.Ldarg_0) stack.Push(playerToken);
                    else if (instruction.opcode == EmitOpCodes.Dup) stack.Push(stack.Peek());
                    else if (IsField(instruction))
                    {
                        if (stack.Pop() != dropToken) throw new InvalidOperationException("Wrong field receiver");
                        stack.Push(native);
                    }
                    else if (instruction.opcode == EmitOpCodes.Call)
                    {
                        if (stack.Pop() != playerToken) throw new InvalidOperationException("Wrong player argument");
                        bool allowed = (bool)stack.Pop();
                        if (stack.Pop() != dropToken) throw new InvalidOperationException("Wrong drop argument");
                        if (Equals(instruction.operand, gate)) stack.Push(allowed && thrower);
                        else if (Equals(instruction.operand, otherGate)) stack.Push(allowed && trash);
                        else throw new InvalidOperationException("Unexpected filter");
                    }
                    else if (instruction.opcode != EmitOpCodes.Ret) throw new InvalidOperationException("Unexpected gate instruction");
                }
                Expect(stack.Count == 1 && (bool)stack.Pop() == (native && thrower && trash),
                    $"combined gate preserves all restrictions: order={fearFirst}, native={native}, thrower={thrower}, trash={trash}");
            }
        }
        foreach (int count in new[] { 0, 2 })
        {
            bool rejected = false;
            try { Apply(Enumerable.Range(0, count).Select(_ => new CodeInstruction(EmitOpCodes.Ldfld, field))); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("expected one ItemDrop.m_autoPickup"); }
            Expect(rejected, "missing or ambiguous native gate is rejected: count=" + count);
        }
    }

    private static bool OtherAutoPickupFilter(ItemDrop drop, bool allowed, Player player) => allowed;

    private static void CheckRescueSpawnContracts(ModuleDefinition game, ModuleDefinition mod)
    {
        MethodDefinition nativeSpawn = Method(game, "Projectile", "SpawnOnHit");
        Expect(nativeSpawn.IsPrivate && !nativeSpawn.IsStatic && nativeSpawn.ReturnType.FullName == "System.Void" &&
            nativeSpawn.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(
                new[] { "UnityEngine.GameObject", "UnityEngine.Collider", "UnityEngine.Vector3" }),
            "original SpawnOnHit has the exact private three-argument contract used by the patch and rescue");
        MethodDefinition fallback = Method(mod, "FearNoSpear.SpearSafetyTracker", "TryDropStoredItem");
        MethodReference drop = Calls(fallback).Single(m => m.DeclaringType.FullName == "ItemDrop" && m.Name == "DropItem");
        Expect(drop.Resolve().IsPublic && drop.Resolve().IsStatic &&
            drop.ReturnType.FullName == "ItemDrop" && drop.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(
                new[] { "ItemDrop/ItemData", "System.Int32", "UnityEngine.Vector3", "UnityEngine.Quaternion" }),
            "rescue fallback calls the original public DropItem overload directly");
        Expect(fallback.Body.ExceptionHandlers.Any(h => h.CatchType?.FullName == "System.Exception") &&
            Calls(fallback).Any(m => m.Name == "LogWarning"),
            "direct fallback still contains and reports spawn exceptions");
        MethodDefinition nativeCall = Method(mod, "FearNoSpear.SpearSafetyTracker", "SpawnThroughValheimPath");
        var code = nativeCall.Body.Instructions;
        Instruction args = code.Single(i => i.OpCode == OpCodes.Newarr);
        Instruction normal = code.Single(i => i.OpCode == OpCodes.Stelem_Ref);
        Expect(args.Previous.OpCode == OpCodes.Ldc_I4_3 &&
            normal.Previous.OpCode == OpCodes.Box && ((TypeReference)normal.Previous.Operand).FullName == "UnityEngine.Vector3" &&
            normal.Previous.Previous.OpCode == OpCodes.Ldarg_1 &&
            normal.Previous.Previous.Previous.OpCode == OpCodes.Ldc_I4_2,
            "native rescue passes null hit object, null collider, and the supplied normal in slot two");
        Expect(Calls(nativeCall).Any(m => m.Name == "Invoke") && nativeCall.Body.ExceptionHandlers.Count > 0 &&
            !Calls(nativeCall).Any(m => m.Name == "GetParameters" || m.Name == "CreateInstance"),
            "private spawn remains a guarded reflection call without speculative parameter construction");
        MethodDefinition pipeline = Method(mod, "FearNoSpear.SpearSafetyTracker", "TrySpawnOriginalItem");
        Expect(pipeline.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "F_groundHitOnly") &&
            pipeline.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_spawnedDrop") &&
            Calls(pipeline).Where(m => m.Name == "SpawnThroughValheimPath" || m.Name == "TryDropStoredItem")
                .Select(m => m.Name).SequenceEqual(new[] { "SpawnThroughValheimPath", "TryDropStoredItem" }),
            "rescue retains the ground-only guard, exact native drop confirmation, and native-first fallback order");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckSpearClassification(Assembly mod)
    {
        Type detector = mod.GetType("FearNoSpear.SpearProjectileDetector", true);
        MethodInfo classify = detector.GetMethod("IsSpearItem", Fields);
        bool IsSpear(ItemDrop.ItemData item) => (bool)classify.Invoke(null, new object[] { item });
        Expect(!IsSpear(null), "missing item cannot be classified as a spear");
        var item = (ItemDrop.ItemData)RuntimeHelpers.GetUninitializedObject(typeof(ItemDrop.ItemData));
        Expect(!IsSpear(item), "item without shared data cannot be classified as a spear");
        item.m_shared = (ItemDrop.ItemData.SharedData)RuntimeHelpers.GetUninitializedObject(typeof(ItemDrop.ItemData.SharedData));
        item.m_shared.m_name = "$item_ordinary";
        foreach (Skills.SkillType skill in Enum.GetValues(typeof(Skills.SkillType)))
        {
            item.m_shared.m_skillType = skill;
            Expect(IsSpear(item) == (skill == Skills.SkillType.Spears),
                "native skill classification without a spear name: " + skill);
        }
        foreach (int value in new[] { -1, int.MinValue, int.MaxValue, 5000 })
        {
            item.m_shared.m_skillType = (Skills.SkillType)value;
            Expect(!IsSpear(item), "undefined/custom skill needs a spear-like name: " + value);
        }
        item.m_shared.m_skillType = Skills.SkillType.Spears;
        item.m_shared.m_name = null;
        Expect(IsSpear(item), "native spear skill does not require a name");
        item.m_shared.m_skillType = Skills.SkillType.Swords;
        foreach (string name in new[] { null, "", "$item_sword_iron", "SP EAR" })
        {
            item.m_shared.m_name = name;
            Expect(!IsSpear(item), "unrelated or missing name does not broaden tracked weapons");
        }
        foreach (string name in new[] { "spear", "SPEAR", "$item_spear_flint", "JC_Reaper_Spear", "spearman" })
        {
            item.m_shared.m_name = name;
            Expect(IsSpear(item), "case-insensitive substring fallback is preserved: " + name);
        }
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
        Expect(reader.ReadInt() == 7, "protocol bumped to 7");
        object copy = network.GetMethod("ReadRecord", Fields).Invoke(null, new object[] { reader });
        Expect((string)Get(copy, "Key") == "drop:123:456", "drop identity roundtrip");
        Vector3 position = (Vector3)Get(copy, "Position");
        Expect(position.x == -123f && position.y == 45f && position.z == 6789f, "position roundtrip");
        Expect((int)Get(copy, "PrefabHash") == 123456 && (int)Get(copy, "Variant") == 2, "icon identity roundtrip");
        Type kind = mod.GetType("FearNoSpear.LocationKind", true);
        Set(record, "Key", "tomb:123:457");
        Set(record, "Kind", Enum.ToObject(kind, 1));
        Set(record, "CreatedTicks", 345678912345L);
        package = new ZPackage();
        network.GetMethod("WriteRecord", Fields).Invoke(null, new[] { package, record });
        copy = network.GetMethod("ReadRecord", Fields).Invoke(null, new object[] { new ZPackage(package.GetArray()) });
        Expect((string)Get(copy, "Key") == "tomb:123:457" && Convert.ToInt32(Get(copy, "Kind")) == 1 &&
            (long)Get(copy, "CreatedTicks") == 345678912345L, "tombstone kind and full timestamp roundtrip");
        var records = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(recordType));
        bool Valid() => (bool)network.GetMethod("AreValidRecords", Fields).Invoke(null, new object[] { records });
        for (int i = 0; i < 10; i++)
        {
            object entry = Activator.CreateInstance(recordType, true);
            Set(entry, "Key", (i < 5 ? "drop:" : "tomb:") + i);
            Set(entry, "Kind", Enum.ToObject(kind, i < 5 ? 0 : 1));
            Set(entry, "CreatedTicks", i < 5 ? 0L : i * 100L);
            records.Add(entry);
        }
        Expect(Valid(), "mixed response accepts five weapons and five tombstones");
        records.Add(records[0]);
        Expect(!Valid(), "duplicate or oversized response rejected");
        records.RemoveAt(10);
        Set(records[0], "Kind", Enum.ToObject(kind, 42));
        Expect(!Valid(), "unknown target kind rejected");
        Set(records[0], "Kind", Enum.ToObject(kind, 0));
        Set(records[0], "CreatedTicks", 1L);
        Expect(!Valid(), "weapon with tombstone timestamp rejected");
        Set(records[0], "CreatedTicks", 0L);
        Set(records[5], "CreatedTicks", -1L);
        Expect(!Valid(), "invalid tombstone creation time rejected");
        Set(records[5], "CreatedTicks", 500L);
        Set(records[0], "Key", "tomb:extra");
        Set(records[0], "Kind", Enum.ToObject(kind, 1));
        Set(records[0], "CreatedTicks", 50L);
        Expect(!Valid(), "six tombstones rejected even with fewer than ten total targets");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Expect(!(bool)network.GetMethod("IsFinite", Fields).Invoke(null, new object[] { new Vector3(invalid, 0f, 0f) }), "invalid position rejected");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckNearestSelection(Assembly mod)
    {
        Type locator = mod.GetType("FearNoSpear.SpearLocator", true);
        Type targetType = mod.GetType("FearNoSpear.SpearLocator+Target", true);
        Type listType = typeof(List<>).MakeGenericType(targetType);
        MethodInfo add = locator.GetMethod("AddSelected", Fields);
        Type kind = mod.GetType("FearNoSpear.LocationKind", true);
        object Target(string key, Vector3 position) => Activator.CreateInstance(targetType, Fields, null,
            new object[] { key, position, null, Enum.ToObject(kind, 0), 0L }, null);

        foreach (int limit in Enumerable.Range(1, 5))
        {
            var targets = (System.Collections.IList)Activator.CreateInstance(listType);
            foreach (int distance in new[] { 7, 3, 6, 1, 2, 5, 4 })
                add.Invoke(null, new object[] { targets, Target("drop:" + distance, new Vector3(distance, 0f, 0f)), Vector3.zero, limit, false });
            Expect(targets.Count == limit, "nearest selection respects limit " + limit);
            Expect(targets.Cast<object>().Select(t => (string)Get(t, "Key"))
                .SequenceEqual(Enumerable.Range(1, limit).Select(i => "drop:" + i)), "nearest selection order at limit " + limit);
            add.Invoke(null, new object[] { targets, Target("drop:1", Vector3.zero), Vector3.zero, limit, false });
            Expect(targets.Count == limit && ((Vector3)Get(targets[0], "Position")).x == 1f,
                "duplicate server record cannot replace the first loaded record at limit " + limit);
            targets.RemoveAt(0);
            add.Invoke(null, new object[] { targets, Target("drop:new", Vector3.zero), Vector3.zero, limit, false });
            Expect((string)Get(targets[0], "Key") == "drop:new", "new target replaces recovered spear at limit " + limit);
        }
        var tied = (System.Collections.IList)Activator.CreateInstance(listType);
        foreach (string key in new[] { "drop:b", "drop:a" })
            add.Invoke(null, new object[] { tied, Target(key, Vector3.one), Vector3.zero, 5, false });
        Expect((string)Get(tied[0], "Key") == "drop:a", "equal-distance targets have deterministic identity order");
        add.Invoke(null, new object[] { tied, Target("", Vector3.zero), Vector3.zero, 5, false });
        Expect(tied.Count == 2, "empty identities are ignored");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckTombstones(Assembly mod)
    {
        Type locator = mod.GetType("FearNoSpear.SpearLocator", true);
        Type targetType = mod.GetType("FearNoSpear.SpearLocator+Target", true);
        Type kind = mod.GetType("FearNoSpear.LocationKind", true);
        var targets = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(targetType));
        MethodInfo add = locator.GetMethod("AddSelected", Fields);
        object Target(string key, long time, float distance) => Activator.CreateInstance(targetType, Fields, null,
            new object[] { key, new Vector3(distance, 0f, 0f), null, Enum.ToObject(kind, 1), time }, null);
        foreach (int limit in new[] { 1, 2, 3, 4, 5, 2, 1, 5 })
        {
            targets.Clear();
            foreach (int time in new[] { 3, 8, 1, 4, 7, 2, 6, 5 })
                add.Invoke(null, new object[] { targets, Target("tomb:" + time, time, time * 1000f), Vector3.zero, limit, true });
            Expect(targets.Cast<object>().Select(t => (long)Get(t, "CreatedTicks"))
                .SequenceEqual(Enumerable.Range(0, limit).Select(i => 8L - i)), "newest tombstone selection respects count " + limit);
        }
        targets.Clear();
        foreach (int time in new[] { 3, 8, 1, 4, 7, 2, 6, 5 })
            add.Invoke(null, new object[] { targets, Target("tomb:" + time, time, time * 1000f), Vector3.zero, 5, true });
        Expect(targets.Cast<object>().Select(t => (long)Get(t, "CreatedTicks")).SequenceEqual(new long[] { 8, 7, 6, 5, 4 }),
            "newest five tombstones selected even when the oldest is nearest");
        add.Invoke(null, new object[] { targets, Target("tomb:8", 999L, 0f), Vector3.zero, 5, true });
        Expect((long)Get(targets[0], "CreatedTicks") == 8L, "duplicate remote tombstone cannot replace the loaded identity");
        targets.RemoveAt(0);
        add.Invoke(null, new object[] { targets, Target("tomb:3", 3, 0f), Vector3.zero, 5, true });
        Expect(targets.Cast<object>().Select(t => (long)Get(t, "CreatedTicks")).SequenceEqual(new long[] { 7, 6, 5, 4, 3 }),
            "recovering newest grave allows the next remaining grave to enter");
        targets.Clear();
        foreach (string key in new[] { "tomb:b", "tomb:a" })
            add.Invoke(null, new object[] { targets, Target(key, 1, 0f), Vector3.zero, 5, true });
        Expect((string)Get(targets[0], "Key") == "tomb:a", "simultaneous tombstones use stable identity ordering");

        Type indicator = mod.GetType("FearNoSpear.SpearIndicator", true);
        MethodInfo age = indicator.GetMethod("FormatAge", Fields);
        string Age(long created, long now, double dayLength) => (string)age.Invoke(null, new object[] { created, now, dayLength });
        long birth = TimeSpan.TicksPerDay;
        Expect(Age(birth, birth + 3000L * TimeSpan.TicksPerSecond, 1200d) == "2d 12h", "age uses in-game days and hours");
        Expect(Age(birth, birth + 6000L * TimeSpan.TicksPerSecond, 2400d) == "2d 12h", "age respects changed world day length");
        Expect(Age(birth, birth, 1200d) == "0d 0h", "new tombstone starts at zero age");
        Expect(Age(birth, birth - 100, 1200d) == "0d 0h", "clock rewind cannot produce negative age");
        Expect(Age(birth, birth + 1150L * TimeSpan.TicksPerSecond, 1200d) == "0d 23h", "last hour before the day boundary");
        Expect(Age(birth, birth + 1200L * TimeSpan.TicksPerSecond, 1200d) == "1d 0h", "day boundary formatting");
        foreach (double invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
            Expect(Age(birth, birth, invalid) == "", "invalid world day length is not formatted");
        Expect(Age(0L, birth, 1200d) == "", "missing creation time is not fabricated");
        Expect(Age(birth, DateTime.MaxValue.Ticks, 1200d) == "9999d+", "extreme age stays within label bounds");

        Type style = mod.GetType("FearNoSpear.FearNoSpearPlugin+IndicatorStyle", true);
        foreach (int value in Enumerable.Range(0, 4))
        {
            object mode = Enum.ToObject(style, value);
            Expect((bool)indicator.GetMethod("HasBeam", Fields).Invoke(null, new[] { mode }) == (value == 0 || value == 1),
                "beam visibility for style " + mode);
            Expect((bool)indicator.GetMethod("HasHud", Fields).Invoke(null, new[] { mode }) == (value == 0 || value == 2),
                "HUD visibility for style " + mode);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckCandidateCache(Assembly mod)
    {
        Type registry = mod.GetType("FearNoSpear.SpearServerRegistry", true);
        MethodInfo refresh = registry.GetMethod("RefreshCandidates", Fields);
        var weapons = (Dictionary<long, List<ZDOID>>)registry.GetField("WeaponIds", Fields).GetValue(null);
        var graves = (Dictionary<long, List<ZDOID>>)registry.GetField("TombstoneIds", Fields).GetValue(null);
        int throwerHash = (int)mod.GetType("FearNoSpear.SpearThrowerMetadata", true).GetField("ThrowerPlayerIdHash", Fields).GetValue(null);
        FieldInfo instance = typeof(ZDOMan).GetField("s_instance", Fields);
        object previous = instance.GetValue(null);
        var manager = (ZDOMan)RuntimeHelpers.GetUninitializedObject(typeof(ZDOMan));
        var world = new Dictionary<ZDOID, ZDO>();
        typeof(ZDOMan).GetField("m_objectsByID", Fields).SetValue(manager, world);
        var ids = new List<ZDOID>();
        var weaponCandidates = new List<ZDOID>();
        var graveCandidates = new List<ZDOID>();
        ZDOID Add(long owner, bool tombstone = false)
        {
            ZDOID id = new ZDOID(24680L, (uint)(ids.Count + 1));
            ids.Add(id);
            (tombstone ? graveCandidates : weaponCandidates).Add(id);
            var zdo = (ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO));
            zdo.m_uid = id;
            world.Add(id, zdo);
            ZDOExtraData.Set(id, tombstone ? ZDOVars.s_owner : throwerHash, owner);
            if (tombstone) ZDOExtraData.Set(id, ZDOVars.s_timeOfDeath, 100L);
            return id;
        }
        try
        {
            // Populate managed fields in the original DLL; no Unity objects or patched DLLs are used.
            instance.SetValue(null, manager);
            ZDOID first = Add(11L);
            ZDOID second = Add(22L);
            ZDOID third = Add(11L);
            Add(0L);
            ZDOID removed = Add(33L);
            world.Remove(removed);
            ZDOID invalid = Add(44L);
            typeof(ZDO).GetField("m_prefab", Fields).SetValue(world[invalid], -1);
            ZDOID grave = Add(11L, true);
            refresh.Invoke(null, new object[] { weapons, weaponCandidates, throwerHash });
            Expect(weapons.Count == 2 && weapons[11L].SequenceEqual(new[] { first, third }) && weapons[22L].SequenceEqual(new[] { second }),
                "shared weapon candidates group exact identities by player and skip missing/invalid/unowned data");
            refresh.Invoke(null, new object[] { graves, graveCandidates, ZDOVars.s_owner });
            Expect(graves.Count == 1 && graves[11L].SequenceEqual(new[] { grave }) && weapons[11L].Count == 2,
                "tombstone lookup uses native owner and stays independent of weapon candidates");

            ZDOExtraData.Set(first, throwerHash, 22L);
            world.Remove(third);
            refresh.Invoke(null, new object[] { weapons, weaponCandidates, throwerHash });
            Expect(!weapons.ContainsKey(11L) && weapons[22L].ToHashSet().SetEquals(new[] { first, second }),
                "refresh replaces stale owner buckets and removes destroyed candidates");
            registry.GetMethod("Clear", Fields).Invoke(null, null);
            Expect(weapons.Count == 0 && graves.Count == 0 &&
                (float)registry.GetField("_nextScanAt", Fields).GetValue(null) == 0f &&
                (float)registry.GetField("_nextTombstoneScanAt", Fields).GetValue(null) == 0f,
                "world/session cleanup empties both caches and resets scan deadlines");
        }
        finally
        {
            MethodInfo releaseLongs = typeof(ZDOExtraData).GetMethod("ReleaseLongs", Fields);
            foreach (ZDOID id in ids) releaseLongs.Invoke(null, new object[] { id });
            instance.SetValue(null, previous);
            registry.GetMethod("Clear", Fields).Invoke(null, null);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckSelectionCache(Assembly mod)
    {
        Type locator = mod.GetType("FearNoSpear.SpearLocator", true);
        Type recordType = mod.GetType("FearNoSpear.SpearLocationRecord", true);
        Type kind = mod.GetType("FearNoSpear.LocationKind", true);
        var dropInstances = (FieldInfo)locator.GetField("DropInstances", Fields).GetValue(null);
        Expect(dropInstances != null && dropInstances.DeclaringType == typeof(ItemDrop) && dropInstances.IsPrivate &&
            dropInstances.IsStatic && dropInstances.FieldType == typeof(List<ItemDrop>),
            "cached reflection resolves the original private loaded-drop registry without publicizing");
        var records = (System.Collections.IList)locator.GetField("ServerRecords", Fields).GetValue(null);
        var selected = (System.Collections.IList)locator.GetField("SelectedTargets", Fields).GetValue(null);
        var suppressed = (Dictionary<string, float>)locator.GetField("PickedUpUntil", Fields).GetValue(null);
        MethodInfo select = locator.GetMethod("SelectTargets", Fields);
        void Select(int weapons, int tombstones, float x = 0f)
            => select.Invoke(null, new object[] { new Vector3(x, 0f, 0f), weapons, tombstones });
        string[] Keys() => selected.Cast<object>().Select(t => (string)Get(t, "Key")).ToArray();
        try
        {
            locator.GetMethod("Clear", Fields).Invoke(null, null);
            for (int i = 1; i <= 7; ++i)
            foreach (bool tombstone in new[] { false, true })
            {
                object record = Activator.CreateInstance(recordType, true);
                Set(record, "Key", (tombstone ? "tomb:" : "drop:") + i);
                Set(record, "Position", new Vector3(i * 10f, 0f, 0f));
                Set(record, "Kind", Enum.ToObject(kind, tombstone ? 1 : 0));
                Set(record, "CreatedTicks", tombstone ? i * 1000L : 0L);
                records.Add(record);
            }
            foreach (int weapons in Enumerable.Range(0, 6))
            foreach (int tombstones in Enumerable.Range(0, 6))
            {
                Select(weapons, tombstones);
                Expect(Keys().SequenceEqual(Enumerable.Range(1, weapons).Select(i => "drop:" + i)
                    .Concat(Enumerable.Range(0, tombstones).Select(i => "tomb:" + (7 - i)))),
                    $"cached selection keeps independent nearest/newest limits: {weapons}/{tombstones}");
            }
            Select(1, 1, 69f);
            Expect(Keys().SequenceEqual(new[] { "drop:7", "tomb:7" }), "selection refresh follows player movement without changing newest grave ordering");
            suppressed["drop:7"] = suppressed["tomb:7"] = float.MaxValue;
            Select(1, 1, 69f);
            Expect(Keys().SequenceEqual(new[] { "drop:6", "tomb:6" }), "recovery suppression excludes exact identities from cached selection");
            records.Clear();
            Select(5, 5);
            Expect(selected.Count == 0, "empty or expired response clears remote selected targets");
            locator.GetField("_selectionDirty", Fields).SetValue(null, false);
            locator.GetField("_selectedWeaponLimit", Fields).SetValue(null, 5);
            locator.GetField("_selectedTombstoneLimit", Fields).SetValue(null, 5);
            locator.GetMethod("Clear", Fields).Invoke(null, null);
            Expect(selected.Count == 0 && (bool)locator.GetField("_selectionDirty", Fields).GetValue(null) &&
                (int)locator.GetField("_selectedWeaponLimit", Fields).GetValue(null) == 0 &&
                (int)locator.GetField("_selectedTombstoneLimit", Fields).GetValue(null) == 0,
                "session cleanup resets selection, limits, and invalidation state");
        }
        finally { locator.GetMethod("Clear", Fields).Invoke(null, null); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckBeamHud(Assembly mod)
    {
        Type indicator = mod.GetType("FearNoSpear.SpearIndicator", true);
        Type style = mod.GetType("FearNoSpear.FearNoSpearPlugin+IndicatorStyle", true);
        Expect(Enum.GetNames(style).SequenceEqual(new[] { "BeamAndHud", "Beam", "Hud", "Off" }), "only the four current display styles are available");
        Expect((float)indicator.GetField("BeamHeight", Fields).GetRawConstantValue() == 500f, "beam height is 500 meters");
        Type kind = mod.GetType("FearNoSpear.LocationKind", true);
        MethodInfo beamColor = indicator.GetMethod("GetBeamColor", Fields);
        Color weaponColor = (Color)beamColor.Invoke(null, new[] { Enum.Parse(kind, "Weapon") });
        Color tombstoneColor = (Color)beamColor.Invoke(null, new[] { Enum.Parse(kind, "Tombstone") });
        Expect(weaponColor.r == 1f && weaponColor.g == 0.85f && weaponColor.b == 0.3f && weaponColor.a == 1f,
            "weapon beam retains its yellow tint and opacity");
        Expect(tombstoneColor.r == 0.8f && tombstoneColor.g == 0.6f && tombstoneColor.b == 1f && tombstoneColor.a == 1f,
            "tombstone beam uses bright lavender with the same opacity");
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
                bool fits = result.Position.x - 96f * scale >= safe.xMin && result.Position.x + 96f * scale <= safe.xMax &&
                    result.Position.y - 48f * scale >= safe.yMin && result.Position.y + 60f * scale <= safe.yMax;
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
        foreach (int count in Enumerable.Range(1, 10))
        foreach (Vector2 desired in new[] { viewport.center, viewport.min, viewport.max })
        {
            float scale = (float)hudScale.Invoke(null, new object[] { viewport, count });
            Vector2 start = new Vector2(Mathf.Clamp(desired.x, viewport.xMin + 100f * scale, viewport.xMax - 100f * scale),
                Mathf.Clamp(desired.y, viewport.yMin + 80f * scale, viewport.yMax - 80f * scale));
            List<Vector2> placed = Enumerable.Repeat(start, count).ToList();
            arrange.Invoke(null, new object[] { viewport, scale, placed });
            for (int i = 0; i < count; ++i)
            {
                Vector2 point = placed[i];
                Expect(placed.Take(i).All(p => Mathf.Abs(point.x - p.x) >= 192f * scale ||
                    Mathf.Abs(point.y - p.y) >= 116f * scale - 0.02f), $"HUD {i + 1}/{count} does not overlap at {viewport.width}x{viewport.height}");
                Expect(point.x - 96f * scale >= viewport.xMin && point.x + 96f * scale <= viewport.xMax &&
                    point.y - 48f * scale >= viewport.yMin && point.y + 60f * scale <= viewport.yMax,
                    "separated HUD remains in the safe viewport");
            }
        }

        System.Random random = new System.Random(42);
        foreach (Rect viewport in viewports)
        {
            bool fits = true;
            float scale = (float)hudScale.Invoke(null, new object[] { viewport, 10 });
            for (int sample = 0; sample < 1000; ++sample)
            {
                List<Vector2> placed = new List<Vector2>();
                for (int i = 0; i < 10; ++i)
                {
                    Vector2 desired = new Vector2(
                        Mathf.Lerp(viewport.xMin + 100f * scale, viewport.xMax - 100f * scale, (float)random.NextDouble()),
                        Mathf.Lerp(viewport.yMin + 80f * scale, viewport.yMax - 80f * scale, (float)random.NextDouble()));
                    placed.Add(desired);
                }
                arrange.Invoke(null, new object[] { viewport, scale, placed });
                for (int i = 0; i < placed.Count; ++i)
                {
                    Vector2 point = placed[i];
                    fits &= placed.Take(i).All(p => Mathf.Abs(point.x - p.x) >= 192f * scale ||
                        Mathf.Abs(point.y - p.y) >= 116f * scale - 0.02f);
                    fits &= point.x - 96f * scale >= viewport.xMin && point.x + 96f * scale <= viewport.xMax &&
                        point.y - 48f * scale >= viewport.yMin && point.y + 60f * scale <= viewport.yMax;
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
