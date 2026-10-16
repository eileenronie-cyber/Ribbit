#if NETFRAMEWORK

// Types and members that .NET 8 provides and .NET Framework 4.8 does not.
//
// Compiled ONLY into the Revit 2024 build. On .NET the real ones exist and a
// second definition would clash with them.
//
// Nothing here adds behaviour. Each one exists so the C# compiler can lower
// syntax the codebase already uses - none of it is a different implementation
// of anything, which is what keeps the two builds honestly the same program.

namespace System
{
    /// <summary>
    /// Needed for range syntax: <c>text[prefix.Length..]</c>.
    ///
    /// The compiler lowers a string range to Substring, so it needs these types
    /// to exist but calls nothing on them at runtime for the uses here.
    /// </summary>
    internal readonly struct Index
    {
        private readonly int _value;

        public Index(int value, bool fromEnd = false)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));

            _value = fromEnd ? ~value : value;
        }

        public int Value => _value < 0 ? ~_value : _value;

        public bool IsFromEnd => _value < 0;

        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;

        public static implicit operator Index(int value) => new Index(value);
    }

    internal readonly struct Range
    {
        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        public Index Start { get; }

        public Index End { get; }

        public static Range StartAt(Index start) => new Range(start, new Index(0, fromEnd: true));

        public static Range EndAt(Index end) => new Range(new Index(0), end);

        public static Range All => new Range(new Index(0), new Index(0, fromEnd: true));
    }
}

namespace Microsoft.Win32
{
    /// <summary>
    /// WPF gained a folder picker in .NET 8. On .NET Framework the only one
    /// available is WinForms' FolderBrowserDialog, wrapped here to the same
    /// shape so the four call sites do not need to know which build they are in.
    ///
    /// Same dialog either way as far as the user is concerned: pick a folder,
    /// or cancel.
    /// </summary>
    internal sealed class OpenFolderDialog
    {
        public string Title { get; set; } = "";

        public string InitialDirectory { get; set; } = "";

        public string FolderName { get; private set; } = "";

        /// <summary>Nullable bool to match the WPF dialog this stands in for.</summary>
        public bool? ShowDialog()
        {
            using System.Windows.Forms.FolderBrowserDialog dialog = new();

            if (Title.Length > 0)
                dialog.Description = Title;

            if (InitialDirectory.Length > 0)
                dialog.SelectedPath = InitialDirectory;

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return false;

            FolderName = dialog.SelectedPath;
            return true;
        }
    }
}

namespace System.Collections.Generic
{
    /// <summary>
    /// Lets <c>foreach ((string key, T value) in dictionary)</c> compile.
    ///
    /// KeyValuePair grew a Deconstruct in .NET Core; on .NET Framework it never
    /// had one, and the loop simply will not build without this.
    /// </summary>
    internal static class KeyValuePairCompat
    {
        public static void Deconstruct<TKey, TValue>(
            this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }

        /// <summary>
        /// Arrived with .NET Core. Same contract: the value, or default when the
        /// key is absent.
        /// </summary>
        public static TValue? GetValueOrDefault<TKey, TValue>(
            this Dictionary<TKey, TValue> source, TKey key)
            where TKey : notnull
            => source.TryGetValue(key, out TValue? value) ? value : default;
    }
}

#endif
