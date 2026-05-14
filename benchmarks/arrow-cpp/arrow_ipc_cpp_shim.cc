#include <arrow/api.h>
#include <arrow/buffer.h>
#include <arrow/io/api.h>
#include <arrow/ipc/api.h>
#include <arrow/result.h>
#include <arrow/status.h>
#include <arrow/util/compression.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <exception>
#include <iomanip>
#include <memory>
#include <sstream>
#include <string>
#include <string_view>
#include <utility>

namespace
{
constexpr int kSuccess = 0;
constexpr int kInvalidArgument = 1;
constexpr int kArrowError = 2;
constexpr int kBufferTooSmall = 3;
constexpr int kUnhandledException = 4;

void CopyError(std::string_view message, char* error, int32_t errorLength)
{
    if (error == nullptr || errorLength <= 0)
    {
        return;
    }

    std::size_t writable = static_cast<std::size_t>(errorLength - 1);
    std::size_t count = std::min(writable, message.size());
    std::memcpy(error, message.data(), count);
    error[count] = '\0';
}

int ReturnArrowError(const arrow::Status& status, char* error, int32_t errorLength)
{
    CopyError(status.ToString(), error, errorLength);
    return kArrowError;
}

arrow::Result<int64_t> ReadRows(const uint8_t* input, int64_t inputLength)
{
    auto buffer = std::make_shared<arrow::Buffer>(input, inputLength);
    arrow::io::BufferReader readerInput(buffer);
    ARROW_ASSIGN_OR_RAISE(std::shared_ptr<arrow::ipc::RecordBatchReader> reader,
                          arrow::ipc::RecordBatchStreamReader::Open(&readerInput));

    int64_t rows = 0;
    while (true)
    {
        std::shared_ptr<arrow::RecordBatch> batch;
        ARROW_RETURN_NOT_OK(reader->ReadNext(&batch));
        if (batch == nullptr)
        {
            break;
        }

        rows += batch->num_rows();
    }

    return rows;
}

arrow::Result<arrow::Compression::type> ParseCodec(int32_t codec)
{
    switch (codec)
    {
    case 1:
        return arrow::Compression::LZ4_FRAME;
    case 2:
        return arrow::Compression::ZSTD;
    default:
        return arrow::Status::Invalid("Unsupported codec id: ", codec);
    }
}

arrow::Result<std::shared_ptr<arrow::RecordBatch>> CreateRecordBatch(int64_t rowCount)
{
    arrow::Int32Builder ids;
    arrow::StringBuilder categories;
    ARROW_RETURN_NOT_OK(ids.Reserve(rowCount));
    ARROW_RETURN_NOT_OK(categories.Reserve(rowCount));

    for (int64_t i = 0; i < rowCount; ++i)
    {
        ARROW_RETURN_NOT_OK(ids.Append(static_cast<int32_t>((i * 31) ^ (i >> 3))));

        std::ostringstream category;
        category << "category-" << std::setfill('0') << std::setw(3) << (i % 128)
                 << "-bucket-" << std::setfill('0') << std::setw(2) << ((i * 17) % 31);
        ARROW_RETURN_NOT_OK(categories.Append(category.str()));
    }

    std::shared_ptr<arrow::Array> idArray;
    std::shared_ptr<arrow::Array> categoryArray;
    ARROW_RETURN_NOT_OK(ids.Finish(&idArray));
    ARROW_RETURN_NOT_OK(categories.Finish(&categoryArray));

    auto schema = arrow::schema({
        arrow::field("id", arrow::int32(), false),
        arrow::field("category", arrow::utf8(), false),
    });

    return arrow::RecordBatch::Make(schema, rowCount, {idArray, categoryArray});
}

struct RecordBatchHandle
{
    std::shared_ptr<arrow::RecordBatch> batch;
};

arrow::Result<std::shared_ptr<arrow::Buffer>> WriteCompressedIpcStream(
    const std::shared_ptr<arrow::RecordBatch>& batch,
    int32_t codec)
{
    ARROW_ASSIGN_OR_RAISE(arrow::Compression::type codecType, ParseCodec(codec));
    ARROW_ASSIGN_OR_RAISE(std::unique_ptr<arrow::util::Codec> codecOwner, arrow::util::Codec::Create(codecType));
    std::shared_ptr<arrow::util::Codec> compressionCodec(std::move(codecOwner));
    ARROW_ASSIGN_OR_RAISE(std::shared_ptr<arrow::io::BufferOutputStream> output,
                          arrow::io::BufferOutputStream::Create());

    arrow::ipc::IpcWriteOptions options = arrow::ipc::IpcWriteOptions::Defaults();
    options.codec = compressionCodec;

    ARROW_ASSIGN_OR_RAISE(std::shared_ptr<arrow::ipc::RecordBatchWriter> writer,
                          arrow::ipc::MakeStreamWriter(output.get(), batch->schema(), options));
    ARROW_RETURN_NOT_OK(writer->WriteRecordBatch(*batch));
    ARROW_RETURN_NOT_OK(writer->Close());
    return output->Finish();
}
}

extern "C"
{
int arrow_cpp_create_record_batch(
    int64_t rowCount,
    void** handle,
    char* error,
    int32_t errorLength)
{
    try
    {
        if (rowCount <= 0 || handle == nullptr)
        {
            CopyError("Invalid row count or handle pointer", error, errorLength);
            return kInvalidArgument;
        }

        arrow::Result<std::shared_ptr<arrow::RecordBatch>> batchResult = CreateRecordBatch(rowCount);
        if (!batchResult.ok())
        {
            return ReturnArrowError(batchResult.status(), error, errorLength);
        }

        *handle = new RecordBatchHandle{*batchResult};
        return kSuccess;
    }
    catch (const arrow::Status& status)
    {
        return ReturnArrowError(status, error, errorLength);
    }
    catch (const std::exception& ex)
    {
        CopyError(ex.what(), error, errorLength);
        return kUnhandledException;
    }
}

void arrow_cpp_free_record_batch(void* handle)
{
    delete static_cast<RecordBatchHandle*>(handle);
}

int arrow_cpp_read_ipc_stream(
    const uint8_t* input,
    int64_t inputLength,
    int64_t expectedRows,
    int64_t* rowsRead,
    char* error,
    int32_t errorLength)
{
    try
    {
        if (input == nullptr || inputLength <= 0 || rowsRead == nullptr)
        {
            CopyError("Invalid input buffer or output pointer", error, errorLength);
            return kInvalidArgument;
        }

        arrow::Result<int64_t> rowsResult = ReadRows(input, inputLength);
        if (!rowsResult.ok())
        {
            return ReturnArrowError(rowsResult.status(), error, errorLength);
        }

        int64_t rows = *rowsResult;
        if (expectedRows >= 0 && rows != expectedRows)
        {
            CopyError("Decoded row count did not match expected row count", error, errorLength);
            return kArrowError;
        }

        *rowsRead = rows;
        return kSuccess;
    }
    catch (const arrow::Status& status)
    {
        return ReturnArrowError(status, error, errorLength);
    }
    catch (const std::exception& ex)
    {
        CopyError(ex.what(), error, errorLength);
        return kUnhandledException;
    }
}

int arrow_cpp_write_ipc_stream_from_batch(
    void* handle,
    int32_t codec,
    uint8_t* output,
    int64_t outputCapacity,
    int64_t* bytesWritten,
    char* error,
    int32_t errorLength)
{
    try
    {
        if (handle == nullptr || output == nullptr || outputCapacity <= 0 || bytesWritten == nullptr)
        {
            CopyError("Invalid batch handle, output buffer, or output pointer", error, errorLength);
            return kInvalidArgument;
        }

        const auto* batchHandle = static_cast<RecordBatchHandle*>(handle);
        arrow::Result<std::shared_ptr<arrow::Buffer>> bufferResult = WriteCompressedIpcStream(batchHandle->batch, codec);
        if (!bufferResult.ok())
        {
            return ReturnArrowError(bufferResult.status(), error, errorLength);
        }

        std::shared_ptr<arrow::Buffer> buffer = *bufferResult;
        if (buffer->size() > outputCapacity)
        {
            *bytesWritten = buffer->size();
            CopyError("Output buffer is too small", error, errorLength);
            return kBufferTooSmall;
        }

        std::memcpy(output, buffer->data(), static_cast<std::size_t>(buffer->size()));
        *bytesWritten = buffer->size();
        return kSuccess;
    }
    catch (const arrow::Status& status)
    {
        return ReturnArrowError(status, error, errorLength);
    }
    catch (const std::exception& ex)
    {
        CopyError(ex.what(), error, errorLength);
        return kUnhandledException;
    }
}

int arrow_cpp_write_ipc_stream(
    int64_t rowCount,
    int32_t codec,
    uint8_t* output,
    int64_t outputCapacity,
    int64_t* bytesWritten,
    char* error,
    int32_t errorLength)
{
    try
    {
        if (rowCount <= 0 || output == nullptr || outputCapacity <= 0 || bytesWritten == nullptr)
        {
            CopyError("Invalid row count, output buffer, or output pointer", error, errorLength);
            return kInvalidArgument;
        }

        arrow::Result<std::shared_ptr<arrow::RecordBatch>> batchResult = CreateRecordBatch(rowCount);
        if (!batchResult.ok())
        {
            return ReturnArrowError(batchResult.status(), error, errorLength);
        }

        arrow::Result<std::shared_ptr<arrow::Buffer>> bufferResult = WriteCompressedIpcStream(*batchResult, codec);
        if (!bufferResult.ok())
        {
            return ReturnArrowError(bufferResult.status(), error, errorLength);
        }

        std::shared_ptr<arrow::Buffer> buffer = *bufferResult;
        if (buffer->size() > outputCapacity)
        {
            *bytesWritten = buffer->size();
            CopyError("Output buffer is too small", error, errorLength);
            return kBufferTooSmall;
        }

        std::memcpy(output, buffer->data(), static_cast<std::size_t>(buffer->size()));
        *bytesWritten = buffer->size();
        return kSuccess;
    }
    catch (const arrow::Status& status)
    {
        return ReturnArrowError(status, error, errorLength);
    }
    catch (const std::exception& ex)
    {
        CopyError(ex.what(), error, errorLength);
        return kUnhandledException;
    }
}
}
