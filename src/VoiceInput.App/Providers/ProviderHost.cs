using System.Reflection;
using System.Runtime.Loader;
using VoiceInput.Core.Diagnostics;
using VoiceInput.Core.Pipeline;
using VoiceInput.Core.Providers;

namespace VoiceInput.App.Providers;

/// <summary>一个被发现并实例化的插件。</summary>
public sealed record LoadedProvider(IAsrProvider Instance, string Source)
{
    public ProviderDescriptor Descriptor => ProviderDescriptor.From(Instance);
}

/// <summary>
/// 从 providers 目录加载识别引擎插件。宿主不认识任何具体厂商：它只找实现了
/// <see cref="IAsrProvider"/> 的公开类型。
///
/// 插件装进默认加载上下文而不是独立上下文：插件必须和宿主共用同一份
/// VoiceInput.Core，否则接口类型会有两份，`is IAsrProvider` 永远为假。
/// 代价是没法卸载——对一个常驻程序来说这个代价为零。
/// </summary>
public sealed class ProviderHost
{
    private readonly List<LoadedProvider> _providers = new();

    public IReadOnlyList<LoadedProvider> Providers => _providers;

    public static ProviderHost Discover(IEnumerable<string> directories, IDiagnosticsLog log)
    {
        var host = new ProviderHost();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = directories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 手工加载的插件不在 deps.json 里，默认上下文不会去目录里按名字找它的依赖，
        // 结果就是插件能加载、第一次碰到依赖类型时才炸（如 sherpa-onnx 的 Dispose）。
        // 这里补一个解析器：任何找不到的托管程序集，依次去插件目录与程序目录找。
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var file in Probe(name))
            {
                try
                {
                    return context.LoadFromAssemblyPath(file);
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
                {
                    log.Warn($"解析 {name.Name} 时加载 {file} 失败：{ex.Message}");
                }
            }

            return null;
        };

        foreach (var directory in roots)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                host.LoadFrom(file, seen, log);
            }
        }

        log.Info($"共发现 {host._providers.Count} 个识别引擎：{string.Join("、", host._providers.Select(p => p.Descriptor.Id))}");
        return host;

        IEnumerable<string> Probe(AssemblyName name)
        {
            var fileName = name.Name;
            if (string.IsNullOrEmpty(fileName))
            {
                yield break;
            }

            foreach (var directory in roots)
            {
                var candidate = Path.Combine(directory, fileName + ".dll");
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }

            var beside = Path.Combine(AppContext.BaseDirectory, fileName + ".dll");
            if (File.Exists(beside))
            {
                yield return beside;
            }
        }
    }

    public LoadedProvider? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return _providers.FirstOrDefault();
        }

        return _providers.FirstOrDefault(p => string.Equals(p.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? _providers.FirstOrDefault();
    }

    /// <summary>
    /// 按配置造转写器。指定引擎不存在时退回第一个可用插件，并记一条日志——
    /// 静默换引擎会让“识别结果不一样了”变成一个查不到原因的怪现象。
    /// </summary>
    public ITranscriber Create(string? providerId, ProviderContext context, IDiagnosticsLog log)
    {
        var provider = Find(providerId) ?? throw new VoiceInputException(
            ErrorCodes.Unexpected,
            ErrorCatalog.Unexpected,
            detail: "providers 目录里没有任何可用的识别引擎");

        if (!string.IsNullOrWhiteSpace(providerId)
            && !string.Equals(provider.Descriptor.Id, providerId, StringComparison.OrdinalIgnoreCase))
        {
            log.Warn($"配置里的识别引擎 {providerId} 不存在，已改用 {provider.Descriptor.Id}。");
        }

        if (provider.Instance.NeedsApiKey && string.IsNullOrWhiteSpace(context.ApiKey))
        {
            throw new VoiceInputException(
                ErrorCodes.ApiKeyMissing,
                $"识别引擎 {provider.Descriptor.DisplayName} 需要 API Key。");
        }

        log.Info($"使用识别引擎 {provider.Descriptor.Id}（{provider.Descriptor.DisplayName}），来源 {provider.Source}。");
        return provider.Instance.Create(context);
    }

    private void LoadFrom(string file, HashSet<string> seen, IDiagnosticsLog log)
    {
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
            var factories = assembly
                .GetTypes()
                .Where(type => type is { IsAbstract: false, IsInterface: false, IsPublic: true }
                    && typeof(IAsrProvider).IsAssignableFrom(type)
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                .ToList();

            if (factories.Count == 0)
            {
                // 目录里还会躺着别的 dll，不算错误，不刷日志。
                return;
            }

            foreach (var type in factories)
            {
                var instance = (IAsrProvider)Activator.CreateInstance(type)!;
                var id = instance.Id;
                if (string.IsNullOrWhiteSpace(id))
                {
                    log.Warn($"{Path.GetFileName(file)} 里的 {type.Name} 没有声明 Id，已跳过。");
                    continue;
                }

                if (!seen.Add(id))
                {
                    log.Warn($"识别引擎 {id} 重复，已保留先加载的那个（{file}）。");
                    continue;
                }

                _providers.Add(new LoadedProvider(instance, Path.GetFileName(file)));
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or TargetInvocationException
                                       or TypeLoadException or ReflectionTypeLoadException)
        {
            log.Warn($"加载 {Path.GetFileName(file)} 失败，已跳过：{ex.Message}");
        }
    }
}
