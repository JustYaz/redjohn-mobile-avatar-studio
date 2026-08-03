using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace MobileAvatarStudio.Editor
{
    internal interface IMeshReductionBackend
    {
        string Name { get; }
        bool IsAvailable { get; }
        string AvailabilityMessage { get; }
        Mesh Reduce(Mesh source, int targetTriangles, bool preserveBlendShapes);
    }

    /// <summary>
    /// Optional adapter for AutoLOD. The package intentionally has no compile-time dependency on
    /// AutoLOD and does not redistribute it. The adapter activates only when AutoLOD is installed
    /// in the current Unity project.
    /// </summary>
    internal sealed class AutoLodReflectionBackend : IMeshReductionBackend
    {
        private const string QualityTypeName = "AutoLOD.MeshDecimator.CQualityMeshDecimator";

        private readonly Type decimatorType;
        private readonly MethodInfo initializeMethod;
        private readonly MethodInfo decimateMethod;

        public AutoLodReflectionBackend()
        {
            decimatorType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(QualityTypeName, false))
                .FirstOrDefault(type => type != null);

            if (decimatorType == null) return;

            initializeMethod = decimatorType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance);
            decimateMethod = decimatorType.GetMethod(
                "DecimateMesh",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(Mesh), typeof(int), typeof(bool) },
                null);
        }

        public string Name => "AutoLOD Quality (optional)";
        public bool IsAvailable => decimatorType != null && initializeMethod != null && decimateMethod != null;
        public string AvailabilityMessage => IsAvailable
            ? "AutoLOD was detected. It will be used only as a locally installed optional backend."
            : "AutoLOD was not detected. Original meshes can still be analyzed, but reduced candidates cannot be generated yet.";

        public Mesh Reduce(Mesh source, int targetTriangles, bool preserveBlendShapes)
        {
            if (!IsAvailable) throw new InvalidOperationException(AvailabilityMessage);
            if (source == null) throw new ArgumentNullException(nameof(source));

            var instance = Activator.CreateInstance(decimatorType);
            initializeMethod.Invoke(instance, null);

            try
            {
                var output = decimateMethod.Invoke(instance, new object[]
                {
                    source,
                    targetTriangles,
                    preserveBlendShapes
                }) as Mesh;

                if (output == null)
                    throw new InvalidOperationException($"AutoLOD returned no mesh for {source.name}.");

                // Some reducers do not copy this data consistently. Reapply the original bind poses;
                // candidate validation verifies that the resulting skinning payload is still coherent.
                output.bindposes = source.bindposes;
                output.RecalculateBounds();
                return output;
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    $"AutoLOD failed while reducing {source.name}: {exception.InnerException?.Message ?? exception.Message}",
                    exception.InnerException ?? exception);
            }
        }
    }
}
