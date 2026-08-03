using System;
using System.Collections.Generic;

namespace ArkFramework
{
    internal static class FrameworkExceptions
    {
        public static InvalidOperationException DuplicateRegistration(
            Type serviceType,
            string ownerId)
        {
            return new InvalidOperationException(
                $"Service type '{serviceType.FullName}' is already registered by owner '{ownerId}'.");
        }

        public static InvalidOperationException MissingRegistration(Type serviceType)
        {
            return new InvalidOperationException(
                $"Service type '{serviceType.FullName}' is not registered.");
        }

        public static InvalidOperationException CircularResolution(
            IReadOnlyList<Type> resolutionPath,
            Type repeatedType)
        {
            var pathStart = 0;
            for (var index = 0; index < resolutionPath.Count; index++)
            {
                if (resolutionPath[index] == repeatedType)
                {
                    pathStart = index;
                    break;
                }
            }

            var path = new string[resolutionPath.Count - pathStart + 1];
            for (var index = pathStart; index < resolutionPath.Count; index++)
            {
                path[index - pathStart] = resolutionPath[index].FullName;
            }

            path[path.Length - 1] = repeatedType.FullName;
            return new InvalidOperationException(
                $"Circular service resolution detected: {string.Join(" -> ", path)}.");
        }

        public static ObjectDisposedException DisposedScope(
            string ownerId,
            Type serviceType = null)
        {
            var message = serviceType == null
                ? $"Module scope '{ownerId}' has been disposed."
                : $"Service type '{serviceType.FullName}' belongs to disposed scope '{ownerId}'.";
            return new ObjectDisposedException(nameof(ModuleScope), message);
        }

        public static InvalidOperationException ScopeDisposalDuringResolution(string ownerId)
        {
            return new InvalidOperationException(
                $"Module scope '{ownerId}' cannot be disposed while one of its services is resolving.");
        }
    }
}
