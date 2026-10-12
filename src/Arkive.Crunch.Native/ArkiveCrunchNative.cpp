#include <cstdint>
#include <cstring>

#include "vendor/crunch/crn_decomp.h"

namespace
{
    constexpr std::uint32_t kAbiVersion = 4;

    enum ArkiveCrunchStatus : int
    {
        Success = 0,
        InvalidArgument = 1,
        InvalidCrn = 2,
        UnsupportedCrn = 3,
        OutputTooSmall = 4,
        UnpackBeginFailed = 5,
        UnpackLevelFailed = 6,
        CrnValidationFailed = 7,
        CrnHeaderTooShort = 8,
        CrnSignatureMismatch = 9,
        CrnHeaderSizeMismatch = 10,
        CrnDeclaredSizeMismatch = 11,
        CrnTextureInfoFailed = 12,
        CrnPackedSignatureMismatch = 13,
    };
}

extern "C"
{
    struct ArkiveCrunchInfo
    {
        std::uint32_t abi_version;
        std::uint32_t width;
        std::uint32_t height;
        std::uint32_t levels;
        std::uint32_t faces;
        std::uint32_t format;
        std::uint32_t four_cc;
        std::uint32_t bytes_per_block;
        std::uint32_t actual_data_size;
        std::uint32_t header_size;
    };

    __declspec(dllexport) std::uint32_t __cdecl arkive_crunch_abi_version()
    {
        return kAbiVersion;
    }

    __declspec(dllexport) int __cdecl arkive_crunch_get_info(
        const void* data,
        const std::uint32_t data_size,
        ArkiveCrunchInfo* output)
    {
        if (data == nullptr || data_size == 0 || output == nullptr)
        {
            return InvalidArgument;
        }

        if (data_size < sizeof(crnd::crn_header))
        {
            return CrnHeaderTooShort;
        }

        const auto* raw = static_cast<const std::uint8_t*>(data);
        if (raw[0] != static_cast<std::uint8_t>('H') ||
            raw[1] != static_cast<std::uint8_t>('x'))
        {
            return CrnSignatureMismatch;
        }

        const auto& header = *static_cast<const crnd::crn_header*>(data);
        if (header.m_sig != crnd::crn_header::cCRNSigValue)
        {
            return CrnPackedSignatureMismatch;
        }

        if (header.m_header_size < sizeof(crnd::crn_header))
        {
            return CrnHeaderSizeMismatch;
        }

        if (data_size < header.m_data_size)
        {
            return CrnDeclaredSizeMismatch;
        }

        crnd::crn_texture_info texture_info;
        if (!crnd::crnd_get_texture_info(data, data_size, &texture_info))
        {
            return CrnTextureInfoFailed;
        }

        crnd::crn_file_info file_info;
        if (!crnd::crnd_validate_file(data, data_size, &file_info))
        {
            return CrnValidationFailed;
        }

        std::memset(output, 0, sizeof(*output));
        output->abi_version = kAbiVersion;
        output->width = texture_info.m_width;
        output->height = texture_info.m_height;
        output->levels = texture_info.m_levels;
        output->faces = texture_info.m_faces;
        output->format = static_cast<std::uint32_t>(texture_info.m_format);
        output->four_cc = crnd::crnd_crn_format_to_fourcc(texture_info.m_format);
        output->bytes_per_block = texture_info.m_bytes_per_block;
        output->actual_data_size = file_info.m_actual_data_size;
        output->header_size = file_info.m_header_size;
        return Success;
    }

    static int arkive_crunch_unpack_block_format(
        const void* data,
        const std::uint32_t data_size,
        void* output,
        const std::uint32_t output_size,
        const crn_format expected_format,
        const std::uint32_t expected_bytes_per_block)
    {
        if (data == nullptr || data_size == 0 || output == nullptr || output_size == 0)
        {
            return InvalidArgument;
        }

        ArkiveCrunchInfo info{};
        const int info_status = arkive_crunch_get_info(data, data_size, &info);
        if (info_status != Success)
        {
            return info_status;
        }

        if (info.levels != 1 || info.faces != 1 ||
            info.format != static_cast<std::uint32_t>(expected_format) ||
            info.bytes_per_block != expected_bytes_per_block)
        {
            return UnsupportedCrn;
        }

        const std::uint32_t blocks_x = (info.width + 3U) / 4U;
        const std::uint32_t blocks_y = (info.height + 3U) / 4U;
        const std::uint64_t required_size =
            static_cast<std::uint64_t>(blocks_x) * blocks_y * info.bytes_per_block;
        if (required_size > output_size)
        {
            return OutputTooSmall;
        }

        crnd::crnd_unpack_context context = crnd::crnd_unpack_begin(data, data_size);
        if (context == nullptr)
        {
            return UnpackBeginFailed;
        }

        void* destinations[1] = { output };
        const bool unpacked = crnd::crnd_unpack_level(
            context,
            destinations,
            output_size,
            blocks_x * info.bytes_per_block,
            0);
        crnd::crnd_unpack_end(context);
        return unpacked ? Success : UnpackLevelFailed;
    }

    __declspec(dllexport) int __cdecl arkive_crunch_unpack_dxt1(
        const void* data,
        const std::uint32_t data_size,
        void* output,
        const std::uint32_t output_size)
    {
        return arkive_crunch_unpack_block_format(
            data,
            data_size,
            output,
            output_size,
            cCRNFmtDXT1,
            8);
    }

    __declspec(dllexport) int __cdecl arkive_crunch_unpack_dxt5(
        const void* data,
        const std::uint32_t data_size,
        void* output,
        const std::uint32_t output_size)
    {
        return arkive_crunch_unpack_block_format(
            data,
            data_size,
            output,
            output_size,
            cCRNFmtDXT5,
            16);
    }

    __declspec(dllexport) int __cdecl arkive_crunch_unpack_dxn_xy(
        const void* data,
        const std::uint32_t data_size,
        void* output,
        const std::uint32_t output_size)
    {
        return arkive_crunch_unpack_block_format(
            data,
            data_size,
            output,
            output_size,
            cCRNFmtDXN_XY,
            16);
    }

}
