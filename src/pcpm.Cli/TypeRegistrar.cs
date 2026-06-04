using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace pcpm.Cli;

/// <summary>
/// Bridges Microsoft.Extensions.DependencyInjection to Spectre.Console.Cli's <see cref="ITypeRegistrar"/>.
/// The provider is built lazily inside the resolver — that way Spectre's late <c>Register</c> calls
/// (which happen during <c>RunAsync</c>) keep working even after <c>Build()</c> has been called.
/// </summary>
public sealed class TypeRegistrar : ITypeRegistrar
{
    private readonly IServiceCollection _services;
    private readonly Lazy<IServiceProvider> _provider;

    public TypeRegistrar(IServiceCollection services)
    {
        _services = services;
        _provider = new Lazy<IServiceProvider>(() => _services.BuildServiceProvider(validateScopes: true));
    }

    public ITypeResolver Build() => new TypeResolver(_provider);

    public void Register(Type service, Type impl) =>
        _services.AddSingleton(service, impl);

    public void RegisterInstance(Type service, object impl) =>
        _services.AddSingleton(service, impl);

    public void RegisterLazy(Type service, Func<object> factory) =>
        _services.AddSingleton(service, _ => factory());
}

internal sealed class TypeResolver : ITypeResolver
{
    private readonly Lazy<IServiceProvider> _provider;

    public TypeResolver(Lazy<IServiceProvider> provider) => _provider = provider;

    public object? Resolve(Type? type) => type is null ? null : _provider.Value.GetService(type);
}
