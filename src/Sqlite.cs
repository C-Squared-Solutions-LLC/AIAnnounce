using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AIAnnounce;

// Read-only queries through the SQLite that ships with Windows (System32\winsqlite3.dll), so reading
// agy's conversation stores adds no dependency. Those stores run in WAL mode: a reader never blocks
// the agent writing to them. Opened per query batch, so no handle is left on a file agy may delete.
sealed class SqliteDb : IDisposable
{
    const string Dll = "winsqlite3.dll";
    const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
    const int SQLITE_OPEN_READONLY = 0x1, SQLITE_OPEN_NOMUTEX = 0x8000;

    static bool unavailable;
    IntPtr db;

    SqliteDb(IntPtr db) => this.db = db;

    public static SqliteDb? OpenReadOnly(string path)
    {
        if (unavailable) return null;
        try
        {
            int rc = sqlite3_open_v2(Utf8(path), out IntPtr handle, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX, IntPtr.Zero);
            if (rc != SQLITE_OK)
            {
                if (handle != IntPtr.Zero) sqlite3_close_v2(handle);
                return null;
            }
            sqlite3_busy_timeout(handle, 200);
            return new SqliteDb(handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            unavailable = true;
            Log.Write("sqlite: " + ex.Message);
            return null;
        }
    }

    // Runs `sql` with ?1, ?2... bound to `args` and hands each row to `onRow`. False if it failed.
    public bool Query(string sql, Action<Row> onRow, params long[] args)
    {
        if (sqlite3_prepare_v2(db, Utf8(sql), -1, out IntPtr stmt, IntPtr.Zero) != SQLITE_OK) return false;
        try
        {
            for (int i = 0; i < args.Length; i++) sqlite3_bind_int64(stmt, i + 1, args[i]);
            while (true)
            {
                int rc = sqlite3_step(stmt);
                if (rc != SQLITE_ROW) return rc == SQLITE_DONE;
                onRow(new Row(stmt));
            }
        }
        finally
        {
            sqlite3_finalize(stmt);
        }
    }

    public long Scalar(string sql, long fallback)
    {
        long value = fallback;
        Query(sql, r => value = r.Int(0));
        return value;
    }

    public void Dispose()
    {
        if (db == IntPtr.Zero) return;
        sqlite3_close_v2(db);
        db = IntPtr.Zero;
    }

    // Valid only inside the callback it was handed to.
    public readonly struct Row
    {
        readonly IntPtr stmt;

        internal Row(IntPtr stmt) => this.stmt = stmt;

        public long Int(int column) => sqlite3_column_int64(stmt, column);

        public byte[] Blob(int column)
        {
            IntPtr data = sqlite3_column_blob(stmt, column);
            int length = sqlite3_column_bytes(stmt, column);  // after the pointer, per the SQLite docs
            if (data == IntPtr.Zero || length <= 0) return Array.Empty<byte>();
            var copy = new byte[length];
            Marshal.Copy(data, copy, 0, length);
            return copy;
        }
    }

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    [DllImport(Dll)] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Dll)] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Dll)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Dll)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr stmt, IntPtr tail);
    [DllImport(Dll)] static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Dll)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Dll)] static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Dll)] static extern long sqlite3_column_int64(IntPtr stmt, int column);
    [DllImport(Dll)] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int column);
    [DllImport(Dll)] static extern int sqlite3_column_bytes(IntPtr stmt, int column);
}
