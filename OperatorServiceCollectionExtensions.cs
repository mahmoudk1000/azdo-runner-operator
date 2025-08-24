using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace AzDORunner;

public static class OperatorServiceCollectionExtensions
{
    public static IServiceCollection AddOperatorControllers(this IServiceCollection services, Action<MvcOptions>? mvcConfigure = null)
    {
        services.AddControllers(mvcConfigure ?? (o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true));
        return services;
    }
}