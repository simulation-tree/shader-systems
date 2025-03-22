using Collections.Generic;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Types;
using Unmanaged;
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

        static ShaderCompiler()
        {
            MetadataRegistry.RegisterType<(Half, Half)>();
            MetadataRegistry.RegisterType<(Half, Half, Half)>();
            MetadataRegistry.RegisterType<(Half, Half, Half, Half)>();
            MetadataRegistry.RegisterType<(double, double)>();
            MetadataRegistry.RegisterType<(double, double, double)>();
            MetadataRegistry.RegisterType<(double, double, double, double)>();
            MetadataRegistry.RegisterType<(byte, byte)>();
            MetadataRegistry.RegisterType<(byte, byte, byte)>();
            MetadataRegistry.RegisterType<(byte, byte, byte, byte)>();
            MetadataRegistry.RegisterType<(sbyte, sbyte)>();
            MetadataRegistry.RegisterType<(sbyte, sbyte, sbyte)>();
            MetadataRegistry.RegisterType<(sbyte, sbyte, sbyte, sbyte)>();
            MetadataRegistry.RegisterType<(short, short)>();
            MetadataRegistry.RegisterType<(short, short, short)>();
            MetadataRegistry.RegisterType<(short, short, short, short)>();
            MetadataRegistry.RegisterType<(ushort, ushort)>();
            MetadataRegistry.RegisterType<(ushort, ushort, ushort)>();
            MetadataRegistry.RegisterType<(ushort, ushort, ushort, ushort)>();
            MetadataRegistry.RegisterType<(int, int)>();
            MetadataRegistry.RegisterType<(int, int, int)>();
            MetadataRegistry.RegisterType<(int, int, int, int)>();
            MetadataRegistry.RegisterType<(uint, uint)>();
            MetadataRegistry.RegisterType<(uint, uint, uint)>();
            MetadataRegistry.RegisterType<(uint, uint, uint, uint)>();
            MetadataRegistry.RegisterType<(long, long)>();
            MetadataRegistry.RegisterType<(long, long, long)>();
            MetadataRegistry.RegisterType<(long, long, long, long)>();
            MetadataRegistry.RegisterType<(ulong, ulong)>();
            MetadataRegistry.RegisterType<(ulong, ulong, ulong)>();
            MetadataRegistry.RegisterType<(ulong, ulong, ulong, ulong)>();
        }

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

        public readonly Span<byte> SPVToGLSL(ReadOnlySpan<byte> bytes)
        {
            ThrowIfDisposed();
            Result result = spvc_context_parse_spirv(spvContext, bytes, out spvc_parsed_ir parsedIr);
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

            result = spvc_compiler_compile(compiler, out string? compileResult);
            if (result != Result.Success || compileResult is null)
            {
                string? error = spvc_context_get_last_error_string(spvContext);
                throw new Exception($"Failed to compile SPIR-V: {error}");
            }

            fixed (char* compileResultPtr = compileResult)
            {
                return new(compileResultPtr, compileResult.Length * 2);
            }
        }

        public readonly void ReadUniformPropertiesFromSPV(ReadOnlySpan<byte> vertexBytes, List<ShaderUniformProperty> list, List<ShaderUniformPropertyMember> members)
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
            Span<spvc_reflected_resource> resourcesSpan = new(resourceList, (int)resourceCount);
            int startIndex = list.Count;
            foreach (spvc_reflected_resource resource in resourcesSpan)
            {
                uint set = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.DescriptorSet);
                uint binding = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Binding);
                //uint location = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Location);
                //uint offset = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Offset);
                ASCIIText256 nameText = spvc_compiler_get_name(compiler, resource.id) ?? string.Empty;
                spvc_type type = spvc_compiler_get_type_handle(compiler, resource.type_id);
                Basetype baseType = spvc_type_get_basetype(type);
                if (baseType == Basetype.Struct)
                {
                    uint baseTypeId = spvc_type_get_base_type_id(type);
                    uint memberCount = spvc_type_get_num_member_types(type);
                    uint size = 0;
                    for (uint m = 0; m < memberCount; m++)
                    {
                        uint memberTypeId = spvc_type_get_member_type(type, m);
                        spvc_type memberType = spvc_compiler_get_type_handle(compiler, memberTypeId);
                        uint vectorSize = spvc_type_get_vector_size(memberType);
                        (Types.Type runtimeType, byte typeSize) = GetRuntimeType(memberType, vectorSize);
                        members.Add(new(nameText, runtimeType, typeSize, new ASCIIText256(spvc_compiler_get_member_name(compiler, baseTypeId, m))));
                        size += typeSize;
                    }

                    ShaderUniformProperty uniformBuffer = new(nameText, (byte)binding, (byte)set, size);
                    list.Insert(startIndex, uniformBuffer);
                }
                else
                {
                    throw new Exception($"Unsupported type: {baseType}");
                }
            }
        }

        public readonly void ReadPushConstantsFromSPV(ReadOnlySpan<byte> vertexBytes, List<ShaderPushConstant> list)
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
            spvc_resources_get_resource_list_for_type(resources, ResourceType.PushConstant, out spvc_reflected_resource* resourceList, out nuint resourceCount);
            Span<spvc_reflected_resource> resourcesSpan = new(resourceList, (int)resourceCount);
            spvc_buffer_range** ranges = stackalloc spvc_buffer_range*[16];
            foreach (spvc_reflected_resource resource in resourcesSpan)
            {
                string name = new(spvc_compiler_get_name(compiler, resource.id));
                spvc_type type = spvc_compiler_get_type_handle(compiler, resource.type_id);
                Basetype baseType = spvc_type_get_basetype(type);
                if (baseType == Basetype.Struct)
                {
                    uint baseTypeId = spvc_type_get_base_type_id(type);
                    nuint rangeCount;
                    spvc_compiler_get_active_buffer_ranges(compiler, resource.id, ranges, &rangeCount);
                    spvc_buffer_range* range = ranges[0];
                    for (uint r = 0; r < rangeCount; r++)
                    {
                        spvc_buffer_range first = range[r];
                        uint memberIndex = first.index;
                        byte* memberName = spvc_compiler_get_member_name(compiler, baseTypeId, memberIndex);
                        ShaderPushConstant pushConstant = new(name, new(memberName), (byte)first.offset, (byte)first.range);
                        list.Insert(0, pushConstant);
                    }
                }
                else
                {
                    throw new Exception($"Unexpected type {baseType} for push constants");
                }
            }
        }

        /// <summary>
        /// Reads all vertex input attributes from the given SPIR-V bytes.
        /// </summary>
        public readonly void ReadVertexInputAttributesFromSPV(ReadOnlySpan<byte> vertexBytes, List<ShaderVertexInputAttribute> list)
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
                (Types.Type runtimeType, byte size) = GetRuntimeType(type, vectorSize);
                ShaderVertexInputAttribute vertexInputAttribute = new(name, location, binding, offset, runtimeType, size);
                list.Add(vertexInputAttribute);
                offset += size;
            }
        }

        public readonly void ReadTexturePropertiesFromSPV(ReadOnlySpan<byte> fragmentBytes, List<ShaderSamplerProperty> list)
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
                //uint location = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Location);
                string name = spvc_compiler_get_name(compiler, resource.id) ?? string.Empty; //todo: efficiency: get the sbyte* pointer exposed
                spvc_type type = spvc_compiler_get_type_handle(compiler, resource.type_id);
                Basetype baseType = spvc_type_get_basetype(type);
                if (baseType == Basetype.SampledImage)
                {
                    ShaderSamplerProperty texture = new(name, (byte)binding, (byte)set);
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
        public readonly Span<byte> GLSLToSPV(ReadOnlySpan<byte> bytes, ShaderType type)
        {
            ThrowIfDisposed();
            ThrowIfUnknownType(type);

            ShaderKind bytesFormat = (ShaderKind)(type - 1);
            string entryPoint = "main";
            using ByteWriter entryPointWriter = new(4);
            entryPointWriter.WriteUTF8(entryPoint);
            Span<byte> emptyStringBytes = stackalloc byte[1];
            emptyStringBytes[0] = default;
            Span<byte> entryPointBytes = entryPointWriter.AsSpan();
            nint result = shaderc_compile_into_spv(pointer, bytes.GetPointer(), (uint)bytes.Length, (int)bytesFormat, emptyStringBytes.GetPointer(), entryPointBytes.GetPointer(), options.address);
            int count = (int)shaderc_result_get_length(result);
            uint errorCount = (uint)shaderc_result_get_num_errors(result);
            if (errorCount > 0)
            {
                throw new Exception(new string((sbyte*)shaderc_result_get_error_message(result)));
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

            return new(shaderc_result_get_bytes(result), count);
        }

        private static (Types.Type type, byte size) GetRuntimeType(spvc_type type, uint vectorSize)
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
                            return (MetadataRegistry.GetType<Half>(), 2);
                        case 2:
                            return (MetadataRegistry.GetType<(Half, Half)>(), 4);
                        case 3:
                            return (MetadataRegistry.GetType<(Half, Half, Half)>(), 6);
                        case 4:
                            return (MetadataRegistry.GetType<(Half, Half, Half, Half)>(), 8);
                    }
                    break;
                case Basetype.Fp32:
                    switch (vectorSize)
                    {
                        case 1:
                            return (MetadataRegistry.GetType<float>(), 4);
                        case 2:
                            return (MetadataRegistry.GetType<Vector2>(), 8);
                        case 3:
                            return (MetadataRegistry.GetType<Vector3>(), 12);
                        case 4:
                            return (MetadataRegistry.GetType<Vector4>(), 16);
                    }
                    break;
                case Basetype.Fp64:
                    switch (vectorSize)
                    {
                        case 1:
                            return (MetadataRegistry.GetType<double>(), 8);
                        case 2:
                            return (MetadataRegistry.GetType<(double, double)>(), 16);
                        case 3:
                            return (MetadataRegistry.GetType<(double, double, double)>(), 24);
                        case 4:
                            return (MetadataRegistry.GetType<(double, double, double, double)>(), 32);
                    }
                    break;
                case Basetype.Int8:
                    switch (vectorSize)
                    {
                        case 1:
                            return (MetadataRegistry.GetType<sbyte>(), 8);
                        case 2:
                            return (MetadataRegistry.GetType<(sbyte, sbyte)>(), 16);
                        case 3:
                            return (MetadataRegistry.GetType<(sbyte, sbyte, sbyte)>(), 24);
                        case 4:
                            return (MetadataRegistry.GetType<(sbyte, sbyte, sbyte, sbyte)>(), 32);
                    }
                    break;
                case Basetype.Int16:
                    switch (vectorSize)
                    {
                        case 1:
                            return (MetadataRegistry.GetType<short>(), 2);
                        case 2:
                            return (MetadataRegistry.GetType<(short, short)>(), 4);
                        case 3:
                            return (MetadataRegistry.GetType<(short, short, short)>(), 6);
                        case 4:
                            return (MetadataRegistry.GetType<(short, short, short, short)>(), 8);
                    }
                    break;
                case Basetype.Int32:
                    switch (vectorSize)
                    {
                        case 1:
                            return (MetadataRegistry.GetType<int>(), 4);
                        case 2:
                            return (MetadataRegistry.GetType<(int, int)>(), 8);
                        case 3:
                            return (MetadataRegistry.GetType<(int, int, int)>(), 12);
                        case 4:
                            return (MetadataRegistry.GetType<(int, int, int, int)>(), 16);
                    }
                    break;
                case Basetype.Int64:
                    switch (vectorSize)
                    {
                        case 1:
                            return (MetadataRegistry.GetType<long>(), 8);
                        case 2:
                            return (MetadataRegistry.GetType<(long, long)>(), 16);
                        case 3:
                            return (MetadataRegistry.GetType<(long, long, long)>(), 24);
                        case 4:
                            return (MetadataRegistry.GetType<(long, long, long, long)>(), 32);
                    }
                    break;
                case Basetype.Boolean:
                    switch (vectorSize)
                    {
                        case 1:
                            return (MetadataRegistry.GetType<bool>(), 1);
                        case 2:
                            return (MetadataRegistry.GetType<(bool, bool)>(), 2);
                        case 3:
                            return (MetadataRegistry.GetType<(bool, bool, bool)>(), 3);
                        case 4:
                            return (MetadataRegistry.GetType<(bool, bool, bool, bool)>(), 4);
                    }
                    break;
                case Basetype.Uint8:
                    switch (vectorSize)
                    {
                        case 1:
                            return (MetadataRegistry.GetType<byte>(), 1);
                        case 2:
                            return (MetadataRegistry.GetType<(byte, byte)>(), 2);
                        case 3:
                            return (MetadataRegistry.GetType<(byte, byte, byte)>(), 3);
                        case 4:
                            return (MetadataRegistry.GetType<(byte, byte, byte, byte)>(), 4);
                    }
                    break;
                case Basetype.Uint16:
                    switch (vectorSize)
                    {
                        case 1:
                            return (MetadataRegistry.GetType<ushort>(), 2);
                        case 2:
                            return (MetadataRegistry.GetType<(ushort, ushort)>(), 4);
                        case 3:
                            return (MetadataRegistry.GetType<(ushort, ushort, ushort)>(), 6);
                        case 4:
                            return (MetadataRegistry.GetType<(ushort, ushort, ushort, ushort)>(), 8);
                    }
                    break;
                case Basetype.Uint32:
                    switch (vectorSize)
                    {
                        case 1:
                            return (MetadataRegistry.GetType<uint>(), 4);
                        case 2:
                            return (MetadataRegistry.GetType<(uint, uint)>(), 8);
                        case 3:
                            return (MetadataRegistry.GetType<(uint, uint, uint)>(), 12);
                        case 4:
                            return (MetadataRegistry.GetType<(uint, uint, uint, uint)>(), 16);
                    }
                    break;
                case Basetype.Uint64:
                    switch (vectorSize)
                    {
                        case 1:
                            return (MetadataRegistry.GetType<ulong>(), 8);
                        case 2:
                            return (MetadataRegistry.GetType<(ulong, ulong)>(), 16);
                        case 3:
                            return (MetadataRegistry.GetType<(ulong, ulong, ulong)>(), 24);
                        case 4:
                            return (MetadataRegistry.GetType<(ulong, ulong, ulong, ulong)>(), 32);
                    }
                    break;
            }

            throw new InvalidOperationException($"Unsupported value type `{baseType}`");
        }

        [Conditional("DEBUG")]
        private static void ThrowIfUnknownType(ShaderType type)
        {
            if ((byte)type >= 4)
            {
                throw new InvalidOperationException($"Unsupported shader type `{type}`");
            }
        }

        public readonly struct Options : IDisposable
        {
            public readonly nint address;

            public Options()
            {
                address = shaderc_compile_options_initialize();
            }

            public readonly void Dispose()
            {
                shaderc_compile_options_release(address);
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
