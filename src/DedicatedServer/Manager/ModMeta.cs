// A mod's name and version, read from its DLL's [assembly: MelonInfo(type, name, version, author)]
// without loading it (System.Reflection.Metadata), so the wizard can show real names and the
// server's OptionalMods list uses the same names the mod does.
using System;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SDServerManager
{
    /// <summary>Where a mod runs: the three choices on the wizard's Mods page and the Mods page.</summary>
    public static class ModUse
    {
        public const string Required = "Server, and every player needs it";
        public const string Optional = "Server; players may leave it out";
        public const string PlayersOnly = "Not on the server (players' own)";
        public static readonly string[] Choices = { Required, Optional, PlayersOnly };

        public const string Help = "For each mod, say where it runs. Server, and every player needs it: a player without it (or with another version) is turned away and told what to install. "
            + "Server; players may leave it out: the server runs it, players choose. Not on the server: the server doesn't load it at all (mods that only change a player's own screen, "
            + "or that misbehave on a server); players may still have it. DedicatedServer always runs and is always needed.";

        public static bool OnServer(string use) { return use == Required || use == Optional; }

        /// <summary>Mods that only change a player's own screen don't go on a server unless asked.</summary>
        public static string Default(string mod)
        {
            if (mod == "DedicatedServer") return Required;
            if (mod == "Demo Verify Harness") return PlayersOnly;
            if (mod == "Demo Ship Tools") return Optional;     // its admin tools work through the server
            return Array.IndexOf(ModMeta.ClientOnly, mod) >= 0 ? PlayersOnly : Required;
        }
    }

    public class ModMeta
    {
        public string File, Name, Version, Author;
        public bool IsPlugin;

        /// <summary>Mods that change only a player's own screen: a server lets players leave them out.</summary>
        public static readonly string[] ClientOnly = { "Demo Verify Harness", "Demo Zoom", "Demo Build Pick", "Demo Mod List", "Demo Ship Tools", "ExtraCameras", "Performance+" };

        public static ModMeta Read(string path)
        {
            ModMeta m = new ModMeta { File = path, Name = Path.GetFileNameWithoutExtension(path), Version = "?", Author = "" };
            try
            {
                using (FileStream fs = System.IO.File.OpenRead(path))
                using (PEReader pe = new PEReader(fs))
                {
                    MetadataReader md = pe.GetMetadataReader();
                    foreach (CustomAttributeHandle h in md.GetAssemblyDefinition().GetCustomAttributes())
                    {
                        CustomAttribute a = md.GetCustomAttribute(h);
                        string type = AttributeType(md, a);
                        if (type != "MelonInfoAttribute") continue;
                        CustomAttributeValue<string> v = a.DecodeValue(new Provider());
                        if (v.FixedArguments.Length > 1) m.Name = v.FixedArguments[1].Value as string ?? m.Name;
                        if (v.FixedArguments.Length > 2) m.Version = v.FixedArguments[2].Value as string ?? "?";
                        if (v.FixedArguments.Length > 3) m.Author = v.FixedArguments[3].Value as string ?? "";
                    }
                }
            }
            catch { }
            return m;
        }

        private static string AttributeType(MetadataReader md, CustomAttribute a)
        {
            if (a.Constructor.Kind == HandleKind.MemberReference)
            {
                MemberReference mr = md.GetMemberReference((MemberReferenceHandle)a.Constructor);
                if (mr.Parent.Kind == HandleKind.TypeReference) return md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name);
            }
            return "";
        }

        private class Provider : ICustomAttributeTypeProvider<string>
        {
            public string GetPrimitiveType(PrimitiveTypeCode typeCode) { return typeCode.ToString(); }
            public string GetSystemType() { return "System.Type"; }
            public string GetSZArrayType(string elementType) { return elementType + "[]"; }
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) { return reader.GetString(reader.GetTypeDefinition(handle).Name); }
            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) { return reader.GetString(reader.GetTypeReference(handle).Name); }
            public string GetTypeFromSerializedName(string name) { return name; }
            public PrimitiveTypeCode GetUnderlyingEnumType(string type) { return PrimitiveTypeCode.Int32; }
            public bool IsSystemType(string type) { return type == "System.Type" || type == "Type"; }
        }
    }
}
