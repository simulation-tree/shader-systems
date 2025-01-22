using Collections;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

        public readonly USpan<byte> SPVToGLSL(USpan<byte> bytes)
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

            sbyte* compileResult = default;
            result = spvc_compiler_compile(compiler, &compileResult);
            if (result != Result.Success || compileResult is null)
            {
                string? error = spvc_context_get_last_error_string(spvContext);
                throw new Exception($"Failed to compile SPIR-V: {error}");
            }

            uint stringLength = 0;
            while (compileResult[stringLength] != 0)
            {
                stringLength++;
            }

            return new USpan<byte>(compileResult, stringLength);
        }

        public readonly void ReadUniformPropertiesFromSPV(USpan<byte> vertexBytes, List<ShaderUniformProperty> list, List<ShaderUniformPropertyMember> members)
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
            USpan<spvc_reflected_resource> resourcesSpan = new(resourceList, (uint)resourceCount);
            uint startIndex = list.Count;
            foreach (spvc_reflected_resource resource in resourcesSpan)
            {
                uint set = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.DescriptorSet);
                uint binding = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Binding);
                //uint location = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Location);
                //uint offset = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Offset);
                FixedString nameText = spvc_compiler_get_name(compiler, resource.id) ?? string.Empty;
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
                        (Type runtimeType, byte typeSize) = GetRuntimeType(memberType, vectorSize);
                        members.Add(new(nameText, runtimeType, typeSize, new FixedString(spvc_compiler_get_member_name(compiler, baseTypeId, m))));
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

        public readonly void ReadPushConstantsFromSPV(USpan<byte> vertexBytes, List<ShaderPushConstant> list)
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
            USpan<spvc_reflected_resource> resourcesSpan = new(resourceList, (uint)resourceCount);
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
                        FixedString memberName = new(new string(spvc_compiler_get_member_name(compiler, baseTypeId, memberIndex)));
                        ShaderPushConstant pushConstant = new(name, memberName, (byte)first.offset, (byte)first.range);
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
        public readonly void ReadVertexInputAttributesFromSPV(USpan<byte> vertexBytes, List<ShaderVertexInputAttribute> list)
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
            USpan<spvc_reflected_resource> resourcesSpan = new(resourceList, (uint)resourceCount);
            byte offset = 0;
            foreach (spvc_reflected_resource resource in resourcesSpan)
            {
                byte location = (byte)spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Location);
                //uint offset = spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Offset); //gives 0
                byte binding = (byte)spvc_compiler_get_decoration(compiler, resource.id, Vortice.SPIRV.SpvDecoration.Binding);
                spvc_type type = spvc_compiler_get_type_handle(compiler, resource.type_id);
                uint vectorSize = spvc_type_get_vector_size(type);
                string name = new(spvc_compiler_get_name(compiler, resource.id));
                (Type runtimeType, byte size) = GetRuntimeType(type, vectorSize);
                ShaderVertexInputAttribute vertexInputAttribute = new(name, location, binding, offset, runtimeType, size);
                list.Add(vertexInputAttribute);
                offset += size;
            }
        }

        public readonly void ReadTexturePropertiesFromSPV(USpan<byte> fragmentBytes, List<ShaderSamplerProperty> list)
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
            USpan<spvc_reflected_resource> resourcesSpan = new(resourceList, (uint)resourceCount);
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
        public readonly USpan<byte> GLSLToSPV(USpan<byte> bytes, ShaderStage shaderStage)
        {
            ThrowIfDisposed();

            ShaderKind bytesFormat = default;
            if (shaderStage == ShaderStage.Fragment)
            {
                bytesFormat = ShaderKind.FragmentShader;
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
                throw new Exception($"Unsupported shader stage {shaderStage}");
            }

            string entryPoint = "main";
            using BinaryWriter entryPointWriter = new(4);
            entryPointWriter.WriteUTF8Text(entryPoint);
            USpan<byte> emptyStringBytes = stackalloc byte[1];
            emptyStringBytes[0] = default;
            USpan<byte> entryPointBytes = entryPointWriter.GetBytes();
            nint result = shaderc_compile_into_spv(pointer, (byte*)bytes.Address, bytes.Length, (int)bytesFormat, (byte*)emptyStringBytes.Address, (byte*)entryPointBytes.Address, options.address);
            uint count = (uint)shaderc_result_get_length(result);
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

            return new USpan<byte>(shaderc_result_get_bytes(result), count);
        }

        private static (Type type, byte size) GetRuntimeType(spvc_type type, uint vectorSize)
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
                            return (typeof(Half), 2);
                        case 2:
                            return (typeof((Half, Half)), 4);
                        case 3:
                            return (typeof((Half, Half, Half)), 6);
                        case 4:
                            return (typeof((Half, Half, Half, Half)), 8);
                    }
                    break;
                case Basetype.Fp32:
                    switch (vectorSize)
                    {
                        case 1:
                            return (typeof(float), 4);
                        case 2:
                            return (typeof(Vector2), 8);
                        case 3:
                            return (typeof(Vector3), 12);
                        case 4:
                            return (typeof(Vector4), 16);
                    }
                    break;
                case Basetype.Fp64:
                    switch (vectorSize)
                    {
                        case 1:
                            return (typeof(double), 8);
                        case 2:
                            return (typeof((double, double)), 16);
                        case 3:
                            return (typeof((double, double, double)), 24);
                        case 4:
                            return (typeof((double, double, double, double)), 32);
                    }
                    break;
                case Basetype.Int8:
                    switch (vectorSize)
                    {
                        case 1:
                            return (typeof(sbyte), 8);
                        case 2:
                            return (typeof((sbyte, sbyte)), 16);
                        case 3:
                            return (typeof((sbyte, sbyte, sbyte)), 24);
                        case 4:
                            return (typeof((sbyte, sbyte, sbyte, sbyte)), 32);
                    }
                    break;
                case Basetype.Int16:
                    switch (vectorSize)
                    {
                        case 1:
                            return (typeof(short), 2);
                        case 2:
                            return (typeof((short, short)), 4);
                        case 3:
                            return (typeof((short, short, short)), 6);
                        case 4:
                            return (typeof((short, short, short, short)), 8);
                    }
                    break;
                case Basetype.Int32:
                    switch (vectorSize)
                    {
                        case 1:
                            return (typeof(int), 4);
                        case 2:
                            return (typeof((int, int)), 8);
                        case 3:
                            return (typeof((int, int, int)), 12);
                        case 4:
                            return (typeof((int, int, int, int)), 16);
                    }
                    break;
                case Basetype.Int64:
                    switch (vectorSize)
                    {
                        case 1:
                            return (typeof(long), 8);
                        case 2:
                            return (typeof((long, long)), 16);
                        case 3:
                            return (typeof((long, long, long)), 24);
                        case 4:
                            return (typeof((long, long, long, long)), 32);
                    }
                    break;
                case Basetype.Boolean:
                    switch (vectorSize)
                    {
                        case 1:
                            return (typeof(bool), 1);
                        case 2:
                            return (typeof((bool, bool)), 2);
                        case 3:
                            return (typeof((bool, bool, bool)), 3);
                        case 4:
                            return (typeof((bool, bool, bool, bool)), 4);
                    }
                    break;
                case Basetype.Uint8:
                    switch (vectorSize)
                    {
                        case 1:
                            return (typeof(byte), 1);
                        case 2:
                            return (typeof((byte, byte)), 2);
                        case 3:
                            return (typeof((byte, byte, byte)), 3);
                        case 4:
                            return (typeof((byte, byte, byte, byte)), 4);
                    }
                    break;
                case Basetype.Uint16:
                    switch (vectorSize)
                    {
                        case 1:
                            return (typeof(ushort), 2);
                        case 2:
                            return (typeof((ushort, ushort)), 4);
                        case 3:
                            return (typeof((ushort, ushort, ushort)), 6);
                        case 4:
                            return (typeof((ushort, ushort, ushort, ushort)), 8);
                    }
                    break;
                case Basetype.Uint32:
                    switch (vectorSize)
                    {
                        case 1:
                            return (typeof(uint), 4);
                        case 2:
                            return (typeof((uint, uint)), 8);
                        case 3:
                            return (typeof((uint, uint, uint)), 12);
                        case 4:
                            return (typeof((uint, uint, uint, uint)), 16);
                    }
                    break;
                case Basetype.Uint64:
                    switch (vectorSize)
                    {
                        case 1:
                            return (typeof(ulong), 8);
                        case 2:
                            return (typeof((ulong, ulong)), 16);
                        case 3:
                            return (typeof((ulong, ulong, ulong)), 24);
                        case 4:
                            return (typeof((ulong, ulong, ulong, ulong)), 32);
                    }
                    break;
            }

            throw new Exception("Unsupported type");
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
