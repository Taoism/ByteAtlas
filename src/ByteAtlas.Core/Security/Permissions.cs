using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ByteAtlas.Native;

namespace ByteAtlas.Security;

/// <summary>One access rule of a file or folder.</summary>
public sealed record AccessEntry(string Identity, SecurityIdentifier? Sid, bool Allow, FileSystemRights Rights, bool Inherited, InheritanceFlags Inheritance, PropagationFlags Propagation)
{
    public string Type => Allow ? "Allow" : "Deny";
    public string RightsText => Permissions.Describe(Rights);
    public string InheritedText => Inherited ? "Inherited" : "Explicit";

    public string AppliesTo => (Inheritance, Propagation) switch
    {
        (InheritanceFlags.None, _) => "This item only",
        (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None) => "This folder, sub folders and files",
        (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly) => "Sub folders and files only",
        (InheritanceFlags.ContainerInherit, PropagationFlags.None) => "This folder and sub folders",
        (InheritanceFlags.ContainerInherit, PropagationFlags.InheritOnly) => "Sub folders only",
        (InheritanceFlags.ObjectInherit, PropagationFlags.None) => "This folder and files",
        (InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly) => "Files only",
        _ => "Special",
    };

    /// <summary>The granular rights (for the "advanced" view).</summary>
    public IEnumerable<string> RightNames => Permissions.Granular.Where(g => (Rights & g.Right) == g.Right).Select(g => g.Name);
}

/// <summary>Permissions of a file or folder: owner, access rules, whether inheritance is on.</summary>
public sealed record PermissionInfo(string Owner, List<AccessEntry> Entries, bool InheritanceProtected, string? Error)
{
    public bool HasExplicit => Entries.Any(e => !e.Inherited);

    /// <summary>"Explicit", "Inherited", "Explicit + inherited" or "Not inherited" (protected) — the Inherited column.</summary>
    public string InheritanceText =>
        Error != null ? "" :
        InheritanceProtected ? (HasExplicit ? "Explicit (inheritance off)" : "None") :
        HasExplicit ? (Entries.Any(e => e.Inherited) ? "Explicit + inherited" : "Explicit") : "Inherited";

    /// <summary>Compact one-line summary: "Administrators: Full · Users: Read & execute".</summary>
    public string Summary => Error ?? string.Join(" · ", Entries
        .GroupBy(e => (e.Identity, e.Allow))
        .Select(g => $"{(g.Key.Allow ? "" : "DENY ")}{Permissions.ShortName(g.Key.Identity)}: {Permissions.Describe(g.Aggregate((FileSystemRights)0, (a, e) => a | e.Rights))}"));
}

[SupportedOSPlatform("windows")]
public static class Permissions
{
    internal static readonly (FileSystemRights Right, string Name)[] Granular =
    [
        (FileSystemRights.ReadData, "List folder / read data"),
        (FileSystemRights.WriteData, "Create files / write data"),
        (FileSystemRights.AppendData, "Create folders / append data"),
        (FileSystemRights.ReadExtendedAttributes, "Read extended attributes"),
        (FileSystemRights.WriteExtendedAttributes, "Write extended attributes"),
        (FileSystemRights.ExecuteFile, "Traverse folder / execute file"),
        (FileSystemRights.DeleteSubdirectoriesAndFiles, "Delete sub folders and files"),
        (FileSystemRights.ReadAttributes, "Read attributes"),
        (FileSystemRights.WriteAttributes, "Write attributes"),
        (FileSystemRights.Delete, "Delete"),
        (FileSystemRights.ReadPermissions, "Read permissions"),
        (FileSystemRights.ChangePermissions, "Change permissions"),
        (FileSystemRights.TakeOwnership, "Take ownership"),
    ];

    // generic rights in ACEs (GENERIC_ALL etc.) mapped to file rights
    private const int GenericAll = 0x10000000, GenericExecute = 0x20000000, GenericWrite = 0x40000000;
    private const int GenericRead = unchecked((int)0x80000000);

    public static FileSystemRights Normalize(FileSystemRights r)
    {
        var v = (int)r;
        if ((v & GenericAll) != 0)
            r |= FileSystemRights.FullControl;
        if ((v & GenericRead) != 0)
            r |= FileSystemRights.Read;
        if ((v & GenericWrite) != 0)
            r |= FileSystemRights.Write;
        if ((v & GenericExecute) != 0)
            r |= FileSystemRights.ReadAndExecute;
        return (FileSystemRights)((int)r & 0x0FFFFFFF);
    }

    /// <summary>The usual names: Full control, Modify, Read &amp; execute, Read, Write, or Special.</summary>
    public static string Describe(FileSystemRights r)
    {
        r = Normalize(r) & ~FileSystemRights.Synchronize;
        var full = FileSystemRights.FullControl & ~FileSystemRights.Synchronize;
        var modify = FileSystemRights.Modify & ~FileSystemRights.Synchronize;
        var readExec = FileSystemRights.ReadAndExecute & ~FileSystemRights.Synchronize;
        var read = FileSystemRights.Read & ~FileSystemRights.Synchronize;
        var write = FileSystemRights.Write & ~FileSystemRights.Synchronize;
        if ((r & full) == full)
            return "Full control";
        if ((r & modify) == modify)
            return "Modify";
        var parts = new List<string>();
        if ((r & readExec) == readExec)
            parts.Add("Read & execute");
        else if ((r & read) == read)
            parts.Add("Read");
        if ((r & write) == write)
            parts.Add("Write");
        return parts.Count > 0 ? string.Join(", ", parts) : r == 0 ? "None" : "Special";
    }

    public static string ShortName(string identity)
    {
        var i = identity.IndexOf('\\');
        return i >= 0 && identity[..i] is "BUILTIN" or "NT AUTHORITY" or "NT SERVICE" ? identity[(i + 1)..] : identity;
    }

    public static PermissionInfo Read(string path)
    {
        try
        {
            FileSystemSecurity sec = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            var owner = sec.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier o ? Win32.AccountName(o) : "";
            var entries = new List<AccessEntry>();
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                var sid = rule.IdentityReference as SecurityIdentifier;
                entries.Add(new AccessEntry(sid != null ? Win32.AccountName(sid) : rule.IdentityReference.Value, sid,
                    rule.AccessControlType == AccessControlType.Allow, Normalize(rule.FileSystemRights), rule.IsInherited,
                    rule.InheritanceFlags, rule.PropagationFlags));
            }
            return new PermissionInfo(owner, entries, sec.AreAccessRulesProtected, null);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException or PrivilegeNotHeldException)
        {
            return new PermissionInfo("", [], false, "(" + e.Message.TrimEnd('.') + ")");
        }
    }

    /// <summary>
    /// SIDs that stand for <paramref name="account"/>: the account itself plus the groups it belongs to
    /// (local and domain groups when the machine is in a domain), plus Everyone / Authenticated Users.
    /// </summary>
    public static HashSet<SecurityIdentifier> SidsOf(string account, out string resolvedName)
    {
        var sids = new HashSet<SecurityIdentifier>();
        resolvedName = account;
        SecurityIdentifier? sid;
        try
        {
            sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        }
        catch (Exception e) when (e is IdentityNotMappedException or SystemException)
        {
            sid = null;
        }
        if (sid == null)
            return sids;
        sids.Add(sid);
        resolvedName = Win32.AccountName(sid);
        sids.Add(new SecurityIdentifier(WellKnownSidType.WorldSid, null));
        sids.Add(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));
        sids.Add(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
        foreach (var g in GroupsOf(resolvedName))
            sids.Add(g);
        return sids;
    }

    private static IEnumerable<SecurityIdentifier> GroupsOf(string account)
    {
        var found = new List<SecurityIdentifier>();
        foreach (var ctx in new[] { System.DirectoryServices.AccountManagement.ContextType.Machine, System.DirectoryServices.AccountManagement.ContextType.Domain })
        {
            try
            {
                using var pc = new System.DirectoryServices.AccountManagement.PrincipalContext(ctx);
                using var p = System.DirectoryServices.AccountManagement.Principal.FindByIdentity(pc, account);
                if (p == null)
                    continue;
                var groups = p is System.DirectoryServices.AccountManagement.UserPrincipal u ? u.GetAuthorizationGroups() : p.GetGroups();
                foreach (var g in groups)
                {
                    if (g.Sid != null)
                        found.Add(g.Sid);
                    g.Dispose();
                }
            }
            catch (Exception e) when (e is System.DirectoryServices.AccountManagement.PrincipalException or System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                // not in a domain / no access: the account's own SID still counts
            }
        }
        return found;
    }

    /// <summary>Rights that the given SIDs have on an item (allow rules minus deny rules).</summary>
    public static FileSystemRights EffectiveFor(PermissionInfo p, HashSet<SecurityIdentifier> sids)
    {
        FileSystemRights allow = 0, deny = 0;
        foreach (var e in p.Entries)
        {
            if (e.Sid == null || !sids.Contains(e.Sid))
                continue;
            if (e.Allow)
                allow |= e.Rights;
            else
                deny |= e.Rights;
        }
        return allow & ~deny;
    }
}
