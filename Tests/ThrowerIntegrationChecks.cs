using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Inspect the actual producer DLLs, without loading their plugins or replacing Unity objects.
internal static class ThrowerIntegrationChecks
{
    internal static void Run(ModuleDefinition fear, string captainPath, string secondaryPath,
        ReaderParameters options, Action<bool, string> expect)
    {
        using var captain = ModuleDefinition.ReadModule(captainPath, options);
        using var secondary = ModuleDefinition.ReadModule(secondaryPath, options);
        TypeDefinition metadata = fear.GetType("FearNoSpear.SpearThrowerMetadata");
        TypeDefinition shield = captain.GetType("CaptainValheim.ShieldRuntimeSystem");
        TypeDefinition copied = secondary.GetType("SecondaryAttacks.CopiedThrowProjectileVisualSystem");
        string captainKey = Constant(metadata, "CaptainThrowerPlayerIdKey");
        string secondaryKey = Constant(metadata, "SecondaryThrowerPlayerIdKey");
        expect(Constant(shield, "ThrowerPlayerIdKey") == captainKey, "CaptainValheim writer and FearNoSpear reader share the exact key");
        expect(Constant(copied, "ThrowerPlayerIdKey") == secondaryKey, "SecondaryAttacks writer and FearNoSpear reader share the exact key");

        foreach (var (type, methodName, key) in new[] {
            (shield, "MarkThrownShield", captainKey), (copied, "MarkRecoverableThrower", secondaryKey) })
        {
            MethodDefinition writer = Method(type, methodName);
            expect(writer.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, key)) &&
                Calls(writer).Any(m => m.DeclaringType.Name == "ZDO" && m.Name == "Set" &&
                    m.Parameters.Last().ParameterType.FullName == "System.Int64"), type.Name + " writes an Int64 ZDO tag");
            expect(Calls(writer).Any(m => m.Name == "IsOwner") && Calls(writer).Any(m => m.Name == "IsValid"),
                type.Name + " only tags a valid, locally owned view");
            expect(!writer.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "m_customData"),
                type.Name + " does not retain restrictions inside inventory item custom data");
            foreach (MethodReference call in Calls(writer).Where(m => m.DeclaringType.Scope.Name == "assembly_valheim"))
                expect(call.Resolve().IsPublic, "new writer uses original public game API: " + call.FullName);
        }

        MethodDefinition consume = Method(shield, "TryConsumeShieldForThrow");
        expect(consume.Parameters.Last().ParameterType.FullName == "System.Int64&" && Calls(consume).Count(m => m.Name == "GetPlayerID") == 1,
            "shield consumption captures the thrower's character ID once");
        TypeDefinition controller = shield.NestedTypes.Single(t => t.Name == "ShieldProjectileController");
        MethodDefinition initialize = Method(controller, "Initialize");
        expect(initialize.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "_throwerPlayerId" &&
            i.Previous.Operand is ParameterDefinition p && p.Name == "throwerPlayerId"),
            "shield controller stores the supplied original thrower, not a later network owner");
        MethodDefinition drop = Method(shield, "DropThrownShield");
        expect(Calls(drop).Count(m => m.DeclaringType.Name == "ItemDrop" && m.Name == "DropItem") == 1 &&
            drop.Body.ExceptionHandlers.Any(h => h.CatchType?.FullName == "System.Exception"),
            "shield drop happens once and metadata errors are contained after spawning");
        foreach (MethodDefinition method in controller.Methods.Where(m => m.HasBody && Calls(m).Any(c => c.Name == "DropThrownShield" || c.Name == "TrySpawnShieldProjectile")))
        {
            int transfers = Calls(method).Count(c => c.Name == "DropThrownShield" || c.Name == "TrySpawnShieldProjectile");
            expect(method.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f && f.Name == "_throwerPlayerId") == transfers,
                "shield drop/chain/return path forwards captured identity: " + method.Name);
            expect(!Calls(method).Any(c => c.Name == "GetPlayerID"), "no late player lookup on shield recovery: " + method.Name);
        }
        expect(Method(shield, "ConfigureShieldProjectileInstance").Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld &&
            i.Operand is FieldReference f && f.Name == "m_respawnItemOnHit" && i.Previous.OpCode == OpCodes.Ldc_I4_0),
            "CaptainValheim still disables native respawning, keeping FearNoSpear rescue out of shield returns");

        MethodDefinition tag = Method(copied, "MarkRecoverableThrower");
        expect(new[] { "m_respawnItemOnHit", "m_spawnItem" }.All(name => tag.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == name)) &&
            Calls(tag).Any(m => m.Name == "GetOwner") && Calls(tag).Any(m => m.Name == "GetPlayerID"),
            "SecondaryAttacks tags only recoverable player-owned projectiles");
        MethodReference owner = Calls(tag).Single(m => m.Name == "GetOwner");
        expect(owner.DeclaringType.FullName == "SecondaryAttacks.ProjectileAccess", "SecondaryAttacks reuses its cached owner accessor");
        var setupCalls = Calls(Method(copied, "TryApplyToProjectileSetup")).Select(m => m.Name).ToList();
        expect(setupCalls.IndexOf("MarkRecoverableThrower") >= 0 && setupCalls.IndexOf("MarkRecoverableThrower") < setupCalls.IndexOf("ApplyCurrentWeaponVisual"),
            "thrower tagging does not depend on whether the visual is replaced");
        foreach (FieldReference field in tag.Body.Instructions.Select(i => i.Operand).OfType<FieldReference>().Where(f => f.DeclaringType.Scope.Name == "assembly_valheim"))
            expect(field.Resolve().IsPublic, "new tag guard uses original public field: " + field.FullName);

        foreach (ModuleDefinition producer in new[] { captain, secondary })
        {
            expect(!producer.AssemblyReferences.Any(r => r.Name == "FearNoSpear"), producer.Name + " has no hard FearNoSpear dependency");
            expect(!producer.GetTypes().SelectMany(t => t.CustomAttributes.Concat(t.Methods.SelectMany(m => m.CustomAttributes)))
                .Where(a => a.AttributeType.Name == "HarmonyPatch").Any(a => a.ConstructorArguments.Any(v => Equals(v.Value, "AutoPickup"))),
                producer.Name + " does not install another auto-pickup patch");
        }
    }

    private static string Constant(TypeDefinition type, string name) => (string)type.Fields.Single(f => f.Name == name).Constant;
    private static MethodDefinition Method(TypeDefinition type, string name) => type.Methods.Single(m => m.Name == name);
    private static System.Collections.Generic.IEnumerable<MethodReference> Calls(MethodDefinition method)
        => method.Body.Instructions.Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt).Select(i => (MethodReference)i.Operand);
}
