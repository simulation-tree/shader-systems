using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unmanaged;
using Unmanaged.Collections;
using Vortice.ShaderCompiler;
using Vortice.SpirvCross;
using static Vortice.SpirvCross.SpirvCrossApi;
using Result = Vortice.SpirvCross.Result;

namespace Shaders.Systems
{
    /// <summary>
    /// Can compile shaders from GLSL to SPV.
    /// </summary>
    public unsafe partial struct ShaderCompiler : IDisposable
    {
        private readonly nint pointer;
        private bool valid;
        private readonly Options options;
        private readonly spvc_context spvContext;

        public readonly bool IsDisposed => !valid;

        public ShaderCompiler()
        {
            pointer = shaderc_compiler_initialize();
            Result result = spvc_context_create(out spvContext);
            if (result != Result.Success)
            {
                throw new Exception($"Failed to create SPIR-V cross compiler: {result}");
            }

            spvc_context_set_error_callback(spvContext, &ErrorCallback, default);
            options = new();
            valid = true;
        }

        [Conditional("DEBUG")]
        private readonly void ThrowIfDisposed()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(ShaderCompiler));
            }
        }

        public void Dispose()
        {
            ThrowIfDisposed();
            options.Dispose();
            spvc_context_destroy(spvContext);
            shaderc_compiler_release(pointer);
            valid = false;
        }

        public readonly ReadOnlySpan<byte> SPVToGLSL(ReadOnlySpan<byte> bytes)
        {
            ThrowIfDisposed();
            var result = spvc_context_parse_spirv(spvContext, bytes, out spvc_parsed_ir parsedIr);
            if (result != Result.Success)
            {
                string? error = spvc_context_get_last_error_string(spvContext);
                throw new Exception($"Failed to parse SPIR-V: {error ?? result.ToString()}");
            }

            result = spvc_context_create_compiler(spvContext, Backend.GLSL, parsedIr, CaptureMode.TakeOwnership, out spvc_compiler compiler);
            if (result != Result.Success)
            {
                string? error = spvc_context_get_last_error_string(spvContext);
                throw new Exception($"Failed to create SPIR-V compiler: {error ?? result.ToString()}");
            }

            spvc_compiler_create_compiler_options(compiler, out spvc_compiler_options options);
            spvc_compiler_options_set_uint(options, CompilerOption.GLSLVersion, 330);
            spvc_compiler_options_set_bool(options, CompilerOption.GLSLES, SPVC_FALSE);
            spvc_compiler_install_compiler_options(compiler, options);

            sbyte* compileResult = default;
            result = spvc_compiler_compile(compiler, &compileResult);
            if (result != Result.Success || compileResult is null)
            {
                string? error = spvc_context_get_last_error_string(spvContext);
                throw new Exception($"Failed to compile SPIR-V: {error}");
            }

            int stringLength = 0;
            while (compileResult[stringLength] != 0)
            {
                stringLength++;
            }

            return new ReadOnlySpan<byte>(compileResult, stringLength);
        }

        public readonly void ReadUniformPropertiesFromSPV(ReadOnlySpan<byte> vertexBytes, UnmanagedList<ShaderUniformProperty> list)
        {
            ThrowIfDisposed();
            Result result = spvc_context_parse_spirv(spvContext, vertexBytes, out spvc_parsed_ir parsedIr);
            if (result != Result.Success)
            {
                string? error = spvc_context_get_last_error_string(spvContext);
                throw new Exception($"Failed to parse SPIR-V: {error ?? result.ToString()}");
            }

            result = spvc_context_create_compiler(spvContext, Backend.GLSL, parsedIr, CaptureMode.TakeOwnership, out spvc_compiler compiler);
            if (result != Result.Success)
            {
                string? error = spvc_context_get_last_error_string(spvContext);
                throw new Exception($"Failed to create SPIR-V compiler: {error ?? result.ToString()}");
            }

            spvc_compiler_create_shader_resources(compiler, out spvc_resources resources);
            spvc_resources_get_resource_list_for_type(resources, ResourceType.UniformBuffer, out spvc_reflected_resource* resourceList, out nuint resourceCount);
            using UnmanagedList<ShaderUniformProperty.Member> membersBuffer = new();
            Span<spvc_reflected_resource> resourcesSpan = new(resourceList, (int)resourceCount);
            uint startIndex = list.Count;
            foreach (spvc_reflected_resource resource in resourcesSpan)
            {
                uint set = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.DescriptorSet);
                uint binding = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Binding);
                uint location = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Location);
                uint offset = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Offset);
                string name = new(spvc_compiler_get_name(compiler, resource.id));
                spvc_type type = spvc_compiler_get_type_handle(compiler, resource.type_id);
                Basetype baseType = spvc_type_get_basetype(type);
                uint baseTypeId = spvc_type_get_base_type_id(type);
                if (baseType == Basetype.Struct)
                {
                    uint memberCount = spvc_type_get_num_member_types(type);
                    membersBuffer.Clear();
                    for (uint m = 0; m < memberCount; m++)
                    {
                        uint memberTypeId = spvc_type_get_member_type(type, m);
                        spvc_type memberType = spvc_compiler_get_type_handle(compiler, memberTypeId);
                        uint vectorSize = spvc_type_get_vector_size(memberType);
                        RuntimeType runtimeType = GetRuntimeType(memberType, vectorSize);
                        string memberName = new(spvc_compiler_get_member_name(compiler, baseTypeId, m));
                        membersBuffer.Add(new(runtimeType, memberName));
                    }

                    ShaderUniformProperty uniformBuffer = new(name, new((byte)set, (byte)binding), membersBuffer.AsSpan());
                    list.Insert(startIndex, uniformBuffer);
                }
                else
                {
                    throw new Exception($"Unsupported type: {baseType}");
                }
            }
        }

        /// <summary>
        /// Reads all vertex input attributes from the given SPIR-V bytes.
        /// </summary>
        public readonly void ReadVertexInputAttributesFromSPV(ReadOnlySpan<byte> vertexBytes, UnmanagedList<ShaderVertexInputAttribute> list)
        {
            ThrowIfDisposed();
            Result result = spvc_context_parse_spirv(spvContext, vertexBytes, out spvc_parsed_ir parsedIr);
            if (result != Result.Success)
            {
                string? error = spvc_context_get_last_error_string(spvContext);
                throw new Exception($"Failed to parse SPIR-V: {error ?? result.ToString()}");
            }

            result = spvc_context_create_compiler(spvContext, Backend.GLSL, parsedIr, CaptureMode.TakeOwnership, out spvc_compiler compiler);
            if (result != Result.Success)
            {
                string? error = spvc_context_get_last_error_string(spvContext);
                throw new Exception($"Failed to create SPIR-V compiler: {error ?? result.ToString()}");
            }

            spvc_compiler_create_shader_resources(compiler, out spvc_resources resources);
            spvc_resources_get_resource_list_for_type(resources, ResourceType.StageInput, out spvc_reflected_resource* resourceList, out nuint resourceCount);
            Span<spvc_reflected_resource> resourcesSpan = new(resourceList, (int)resourceCount);
            byte offset = 0;
            foreach (spvc_reflected_resource resource in resourcesSpan)
            {
                byte location = (byte)spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Location);
                //uint offset = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Offset); //gives 0
                byte binding = (byte)spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Binding);
                spvc_type type = spvc_compiler_get_type_handle(compiler, resource.type_id);
                uint vectorSize = spvc_type_get_vector_size(type);
                string name = new(spvc_compiler_get_name(compiler, resource.id));
                RuntimeType runtimeType = GetRuntimeType(type, vectorSize);
                ShaderVertexInputAttribute vertexInputAttribute = new(name, location, binding, offset, runtimeType);
                list.Add(vertexInputAttribute);
                offset += (byte)runtimeType.Size;
            }
        }

        public readonly void ReadTexturePropertiesFromSPV(ReadOnlySpan<byte> fragmentBytes, UnmanagedList<ShaderSamplerProperty> list)
        {
            ThrowIfDisposed();
            Result result = spvc_context_parse_spirv(spvContext, fragmentBytes, out spvc_parsed_ir parsedIr);
            if (result != Result.Success)
            {
                string? error = spvc_context_get_last_error_string(spvContext);
                throw new Exception($"Failed to parse SPIR-V: {error ?? result.ToString()}");
            }

            result = spvc_context_create_compiler(spvContext, Backend.GLSL, parsedIr, CaptureMode.TakeOwnership, out spvc_compiler compiler);
            if (result != Result.Success)
            {
                string? error = spvc_context_get_last_error_string(spvContext);
                throw new Exception($"Failed to create SPIR-V compiler: {error ?? result.ToString()}");
            }

            spvc_compiler_create_shader_resources(compiler, out spvc_resources resources);
            spvc_resources_get_resource_list_for_type(resources, ResourceType.SampledImage, out spvc_reflected_resource* resourceList, out nuint resourceCount);
            Span<spvc_reflected_resource> resourcesSpan = new(resourceList, (int)resourceCount);
            foreach (spvc_reflected_resource resource in resourcesSpan)
            {
                uint set = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.DescriptorSet);
                uint binding = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Binding);
                uint location = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Location);
                string name = new(spvc_compiler_get_name(compiler, resource.id)); //todo: get the sbyte* pointer exposed
                spvc_type type = spvc_compiler_get_type_handle(compiler, resource.type_id);
                Basetype baseType = spvc_type_get_basetype(type);
                if (baseType == Basetype.SampledImage)
                {
                    ShaderSamplerProperty texture = new(name, new((byte)set, (byte)binding));
                    list.Add(texture);
                }
                else
                {
                    throw new Exception($"Unsupported type: {baseType}");
                }
            }
        }

        /// <summary>
        /// Converts the given UTF8 bytes from GLSL to SPIR-V.
        /// </summary>
        public readonly ReadOnlySpan<byte> GLSLToSPV(ReadOnlySpan<byte> bytes, ShaderStage shaderStage)
        {
            ThrowIfDisposed();

            ShaderKind bytesFormat = default;
            if (shaderStage == ShaderStage.Fragment)
            {
                bytesFormat = ShaderKind.GLSLDefaultFragmentShader;
            }
            else if (shaderStage == ShaderStage.Vertex)
            {
                bytesFormat = ShaderKind.VertexShader;
            }
            else if (shaderStage == ShaderStage.Compute)
            {
                bytesFormat = ShaderKind.ComputeShader;
            }
            else if (shaderStage == ShaderStage.Geometry)
            {
                bytesFormat = ShaderKind.GeometryShader;
            }
            else
            {
                throw new Exception("Unsupported shader stage");
            }

            string entryPoint = "main";
            using BinaryWriter entryPointWriter = new();
            entryPointWriter.WriteUTF8Span(entryPoint);
            Span<byte> emptyStringBytes = stackalloc byte[1];
            fixed (byte* entryPointPtr = entryPointWriter.AsSpan())
            {
                fixed (byte* emptyStringPtr = emptyStringBytes)
                {
                    fixed (byte* source = bytes)
                    {
                        nint result = shaderc_compile_into_spv(pointer, source, (nuint)bytes.Length, (int)bytesFormat, emptyStringPtr, entryPointPtr, options.pointer);
                        int count = (int)shaderc_result_get_length(result);
                        uint errorCount = (uint)shaderc_result_get_num_errors(result);
                        if (errorCount > 0)
                        {
                            string errorMessage = new((sbyte*)shaderc_result_get_error_message(result));
                            throw new Exception($"Failed to compile shader: {errorMessage}");
                        }
                        else if (count == 0)
                        {
                            throw new Exception("Failed to compile shader: empty result");
                        }
                        else if (result == 0)
                        {
                            Status status = shaderc_result_get_compilation_status(pointer);
                            throw new Exception($"Failed to compile shader: {status}");
                        }

                        return new Span<byte>(shaderc_result_get_bytes(result), count);
                    }
                }
            }
        }

        private static RuntimeType GetRuntimeType(spvc_type type, uint vectorSize)
        {
            if (vectorSize == 0)
            {
                throw new Exception("Vector size must be greater than 0");
            }

            Basetype baseType = spvc_type_get_basetype(type);
            switch (baseType)
            {
                case Basetype.Fp16:
                    switch (vectorSize)
                    {
                        case 1:
                            return RuntimeType.Get<Half>();
                        case 2:
                            return RuntimeType.Get<(Half, Half)>();
                        case 3:
                            return RuntimeType.Get<(Half, Half, Half)>();
                        case 4:
                            return RuntimeType.Get<(Half, Half, Half, Half)>();
                    }
                    break;
                case Basetype.Fp32:
                    switch (vectorSize)
                    {
                        case 1:
                            return RuntimeType.Get<float>();
                        case 2:
                            return RuntimeType.Get<Vector2>();
                        case 3:
                            return RuntimeType.Get<Vector3>();
                        case 4:
                            return RuntimeType.Get<Vector4>();
                    }
                    break;
                case Basetype.Fp64:
                    switch (vectorSize)
                    {
                        case 1:
                            return RuntimeType.Get<double>();
                        case 2:
                            return RuntimeType.Get<(double, double)>();
                        case 3:
                            return RuntimeType.Get<(double, double, double)>();
                        case 4:
                            return RuntimeType.Get<(double, double, double, double)>();
                    }
                    break;
                case Basetype.Int8:
                    switch (vectorSize)
                    {
                        case 1:
                            return RuntimeType.Get<sbyte>();
                        case 2:
                            return RuntimeType.Get<(sbyte, sbyte)>();
                        case 3:
                            return RuntimeType.Get<(sbyte, sbyte, sbyte)>();
                        case 4:
                            return RuntimeType.Get<(sbyte, sbyte, sbyte, sbyte)>();
                    }
                    break;
                case Basetype.Int16:
                    switch (vectorSize)
                    {
                        case 1:
                            return RuntimeType.Get<short>();
                        case 2:
                            return RuntimeType.Get<(short, short)>();
                        case 3:
                            return RuntimeType.Get<(short, short, short)>();
                        case 4:
                            return RuntimeType.Get<(short, short, short, short)>();
                    }
                    break;
                case Basetype.Int32:
                    switch (vectorSize)
                    {
                        case 1:
                            return RuntimeType.Get<int>();
                        case 2:
                            return RuntimeType.Get<(int, int)>();
                        case 3:
                            return RuntimeType.Get<(int, int, int)>();
                        case 4:
                            return RuntimeType.Get<(int, int, int, int)>();
                    }
                    break;
                case Basetype.Int64:
                    switch (vectorSize)
                    {
                        case 1:
                            return RuntimeType.Get<long>();
                        case 2:
                            return RuntimeType.Get<(long, long)>();
                        case 3:
                            return RuntimeType.Get<(long, long, long)>();
                        case 4:
                            return RuntimeType.Get<(long, long, long, long)>();
                    }
                    break;
                case Basetype.Boolean:
                    switch (vectorSize)
                    {
                        case 1:
                            return RuntimeType.Get<bool>();
                        case 2:
                            return RuntimeType.Get<(bool, bool)>();
                        case 3:
                            return RuntimeType.Get<(bool, bool, bool)>();
                        case 4:
                            return RuntimeType.Get<(bool, bool, bool, bool)>();
                    }
                    break;
                case Basetype.Uint8:
                    switch (vectorSize)
                    {
                        case 1:
                            return RuntimeType.Get<byte>();
                        case 2:
                            return RuntimeType.Get<(byte, byte)>();
                        case 3:
                            return RuntimeType.Get<(byte, byte, byte)>();
                        case 4:
                            return RuntimeType.Get<(byte, byte, byte, byte)>();
                    }
                    break;
                case Basetype.Uint16:
                    switch (vectorSize)
                    {
                        case 1:
                            return RuntimeType.Get<ushort>();
                        case 2:
                            return RuntimeType.Get<(ushort, ushort)>();
                        case 3:
                            return RuntimeType.Get<(ushort, ushort, ushort)>();
                        case 4:
                            return RuntimeType.Get<(ushort, ushort, ushort, ushort)>();
                    }
                    break;
                case Basetype.Uint32:
                    switch (vectorSize)
                    {
                        case 1:
                            return RuntimeType.Get<uint>();
                        case 2:
                            return RuntimeType.Get<(uint, uint)>();
                        case 3:
                            return RuntimeType.Get<(uint, uint, uint)>();
                        case 4:
                            return RuntimeType.Get<(uint, uint, uint, uint)>();
                    }
                    break;
                case Basetype.Uint64:
                    switch (vectorSize)
                    {
                        case 1:
                            return RuntimeType.Get<ulong>();
                        case 2:
                            return RuntimeType.Get<(ulong, ulong)>();
                        case 3:
                            return RuntimeType.Get<(ulong, ulong, ulong)>();
                        case 4:
                            return RuntimeType.Get<(ulong, ulong, ulong, ulong)>();
                    }
                    break;
            }

            throw new Exception("Unsupported type");
        }

        public readonly struct Options : IDisposable
        {
            public readonly nint pointer;

            public Options()
            {
                pointer = shaderc_compile_options_initialize();
            }

            public readonly void Dispose()
            {
                shaderc_compile_options_release(pointer);
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void ErrorCallback(nint userData, sbyte* error)
        {
            throw new Exception($"SPIR-V cross error: {new(error)}");
        }

        [LibraryImport("shaderc_shared")]
        internal static partial nint shaderc_compiler_initialize();

        [LibraryImport("shaderc_shared")]
        internal static partial void shaderc_compiler_release(nint pointer);

        [LibraryImport("shaderc_shared")]
        internal static unsafe partial nint shaderc_compile_into_spv(nint pointer, byte* source, nuint source_size, int shader_kind, byte* input_file, byte* entry_point, nint additional_options);

        [LibraryImport("shaderc_shared")]
        internal static partial nuint shaderc_result_get_length(nint poiter);

        [LibraryImport("shaderc_shared")]
        internal static partial nint shaderc_compile_options_initialize();

        [LibraryImport("shaderc_shared")]
        internal static partial void shaderc_compile_options_release(nint pointer);

        [DllImport("shaderc_shared", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong shaderc_result_get_num_warnings(nint pointer);

        [DllImport("shaderc_shared", CallingConvention = CallingConvention.Cdecl)]
        internal static extern ulong shaderc_result_get_num_errors(nint pointer);

        [DllImport("shaderc_shared", CallingConvention = CallingConvention.Cdecl)]
        internal static extern Status shaderc_result_get_compilation_status(nint pointer);

        [DllImport("shaderc_shared", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void* shaderc_result_get_bytes(nint pointer);

        [DllImport("shaderc_shared", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void* shaderc_result_get_error_message(nint pointer);

        public enum Status
        {
            Success,
            InvalidStage,
            CompilationError,
            InternalError,
            NullResultObject,
            InvalidAssembly,
            ValidationError,
            TransformationError,
            ConfigurationError
        }
    }
}
