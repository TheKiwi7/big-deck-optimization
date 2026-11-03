// dotnet-trace convert with a 1 GB stack: on traces with many card plays it overflows the main thread's stack (StartStopActivity's
// recursive GetActivityStack, thousands of frames deep) and writes nothing.
//   dotnet run --project tools/profiling/bigconvert -- <nettrace> <output.json>
// Writes <output>.speedscope.json, as dotnet-trace does.
using System.Reflection;
using System.Runtime.Loader;

string dll = Directory.GetFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", ".store", "dotnet-trace"), "dotnet-trace.dll", SearchOption.AllDirectories)
    .OrderByDescending(f => f).First();
var resolver = new AssemblyDependencyResolver(dll);
AssemblyLoadContext.Default.Resolving += (context, name) => resolver.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;
AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) => resolver.ResolveUnmanagedDllToPath(name) is { } path ? System.Runtime.InteropServices.NativeLibrary.Load(path) : IntPtr.Zero;
MethodInfo entry = Assembly.LoadFrom(dll).EntryPoint ?? throw new InvalidOperationException("no entry point in " + dll);
int exit = 1;
var thread = new Thread(() =>
{
    object? result = entry.Invoke(null, new object[] { new[] { "convert", "--format", "speedscope", "-o", args[1], args[0] } });
    exit = result switch
    {
        int code => code,
        Task<int> task => task.GetAwaiter().GetResult(),
        _ => 0,
    };
}, 1024 * 1024 * 1024);
thread.Start();
thread.Join();
return exit;
