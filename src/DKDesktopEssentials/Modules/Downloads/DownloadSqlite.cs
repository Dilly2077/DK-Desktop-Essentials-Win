using System.Runtime.InteropServices;

namespace DKDesktopEssentials.Modules.Downloads;

/// <summary>Small parameterized binding to the SQLite supplied by Windows; never load an app-local DLL.</summary>
internal sealed class DownloadSqlite : IDisposable
{
    private nint _db;
    internal DownloadSqlite(string path)
    {
        var code = Open(path, out _db, 2 | 4 | 0x10000, 0); // read/write, create, FULLMUTEX; URI mode off
        if (code != 0) { Dispose(); throw new DownloadEngineException(DownloadErrorCode.StorageFailure); }
    }
    internal void Exec(string sql)
    { if (Execute(_db, sql, 0, 0, 0) != 0) throw new DownloadEngineException(DownloadErrorCode.StorageFailure); }
    internal Statement Prepare(string sql, params object?[] values)
    {
        if (PrepareNative(_db, sql, -1, out var statement, 0) != 0)
            throw new DownloadEngineException(DownloadErrorCode.StorageFailure);
        var result = new Statement(statement);
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var code = values[i] switch
                {
                    null => BindNull(statement, i + 1),
                    byte[] bytes => BindBlob(statement, i + 1, bytes, bytes.Length, -1),
                    string text => BindText(statement, i + 1, text, -1, -1),
                    int number => BindLong(statement, i + 1, number),
                    long number => BindLong(statement, i + 1, number),
                    _ => throw new ArgumentException("Unsupported database parameter.")
                };
                if (code != 0) throw new DownloadEngineException(DownloadErrorCode.StorageFailure);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    internal int Changes => ChangesNative(_db);
    public void Dispose() { if (_db != 0) { Close(_db); _db = 0; } }
    internal sealed class Statement(nint handle) : IDisposable
    {
        internal bool Read()
        {
            var code = Step(handle);
            return code == 100 ? true : code == 101 ? false : throw new DownloadEngineException(DownloadErrorCode.StorageFailure);
        }
        internal long Number(int column) => ColumnLong(handle, column);
        internal string Text(int column) => Marshal.PtrToStringUTF8(ColumnText(handle, column)) ?? "";
        internal byte[] Bytes(int column)
        {
            var count = ColumnBytes(handle, column);
            if (count <= 0 || count > 64 * 1024) throw new DownloadEngineException(DownloadErrorCode.StorageFailure);
            var bytes = new byte[count]; Marshal.Copy(ColumnBlob(handle, column), bytes, 0, count); return bytes;
        }
        public void Dispose() => FinalizeNative(handle);
    }
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int Close(nint db);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int Execute(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint arg, nint error);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int PrepareNative(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint statement, nint tail);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int Step(nint statement);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int FinalizeNative(nint statement);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_int64", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int BindLong(nint statement, int index, long value);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_null", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int BindNull(nint statement, int index);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int BindText(nint statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int length, nint destructor);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_blob", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int BindBlob(nint statement, int index, byte[] bytes, int length, nint destructor);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern long ColumnLong(nint statement, int index);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint ColumnText(nint statement, int index);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint ColumnBlob(nint statement, int index);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ColumnBytes(nint statement, int index);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_changes", CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ChangesNative(nint db);
}
