#include <arrow/api.h>
#include <arrow/buffer.h>
#include <arrow/io/api.h>
#include <arrow/ipc/api.h>
#include <arrow/result.h>
#include <arrow/status.h>
#include <arrow/util/compression.h>

#include <chrono>
#include <cstdint>
#include <exception>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <memory>
#include <numeric>
#include <span>
#include <sstream>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace
{
constexpr int64_t kRowCounts[] = {100'000, 500'000, 1'000'000};
constexpr std::string_view kCodecs[] = {"lz4frame", "zstd"};

struct Options
{
    bool validate = false;
    bool benchmark = false;
    int iterations = 10;
    std::filesystem::path fixtures;
};

std::string FixtureFileName(int64_t rows, std::string_view codec)
{
    std::ostringstream builder;
    builder << "arrow-ipc-" << rows << '-' << codec << ".arrow";
    return builder.str();
}

arrow::Result<std::shared_ptr<arrow::Buffer>> ReadFile(const std::filesystem::path& path)
{
    std::ifstream stream(path, std::ios::binary | std::ios::ate);
    if (!stream)
    {
        return arrow::Status::IOError("Failed to open fixture: ", path.string());
    }

    std::streamsize size = stream.tellg();
    if (size < 0)
    {
        return arrow::Status::IOError("Failed to read fixture size: ", path.string());
    }

    stream.seekg(0, std::ios::beg);
    ARROW_ASSIGN_OR_RAISE(std::shared_ptr<arrow::ResizableBuffer> buffer, arrow::AllocateResizableBuffer(size));

    if (!stream.read(reinterpret_cast<char*>(buffer->mutable_data()), size))
    {
        return arrow::Status::IOError("Failed to read fixture bytes: ", path.string());
    }

    return buffer;
}

arrow::Result<int64_t> ReadRows(const std::shared_ptr<arrow::Buffer>& buffer)
{
    arrow::io::BufferReader input(buffer);
    ARROW_ASSIGN_OR_RAISE(std::shared_ptr<arrow::ipc::RecordBatchReader> reader,
                          arrow::ipc::RecordBatchStreamReader::Open(&input));

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

arrow::Result<arrow::Compression::type> ParseCodec(std::string_view codec)
{
    if (codec == "lz4frame")
    {
        return arrow::Compression::LZ4_FRAME;
    }

    if (codec == "zstd")
    {
        return arrow::Compression::ZSTD;
    }

    return arrow::Status::Invalid("Unsupported codec: ", std::string(codec));
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

arrow::Result<std::shared_ptr<arrow::Buffer>> WriteCompressedIpcStream(
    const std::shared_ptr<arrow::RecordBatch>& batch,
    std::string_view codecName)
{
    ARROW_ASSIGN_OR_RAISE(arrow::Compression::type codecType, ParseCodec(codecName));
    ARROW_ASSIGN_OR_RAISE(std::unique_ptr<arrow::util::Codec> codecOwner, arrow::util::Codec::Create(codecType));
    std::shared_ptr<arrow::util::Codec> codec(std::move(codecOwner));
    ARROW_ASSIGN_OR_RAISE(std::shared_ptr<arrow::io::BufferOutputStream> output,
                          arrow::io::BufferOutputStream::Create());

    arrow::ipc::IpcWriteOptions options = arrow::ipc::IpcWriteOptions::Defaults();
    options.codec = codec;

    ARROW_ASSIGN_OR_RAISE(std::shared_ptr<arrow::ipc::RecordBatchWriter> writer,
                          arrow::ipc::MakeStreamWriter(output.get(), batch->schema(), options));

    ARROW_RETURN_NOT_OK(writer->WriteRecordBatch(*batch));
    ARROW_RETURN_NOT_OK(writer->Close());
    return output->Finish();
}

arrow::Status ValidateFixture(const std::filesystem::path& fixtures, int64_t rows, std::string_view codec)
{
    std::filesystem::path path = fixtures / FixtureFileName(rows, codec);
    ARROW_ASSIGN_OR_RAISE(std::shared_ptr<arrow::Buffer> buffer, ReadFile(path));
    ARROW_ASSIGN_OR_RAISE(int64_t actualRows, ReadRows(buffer));

    if (actualRows != rows)
    {
        return arrow::Status::Invalid("Fixture ", path.string(), " decoded ", actualRows,
                                     " rows, expected ", rows);
    }

    std::cout << "validated," << rows << ',' << codec << ',' << path.string() << '\n';
    return arrow::Status::OK();
}

arrow::Status BenchmarkReadFixture(const std::filesystem::path& fixtures, int64_t rows, std::string_view codec, int iterations)
{
    std::filesystem::path path = fixtures / FixtureFileName(rows, codec);
    ARROW_ASSIGN_OR_RAISE(std::shared_ptr<arrow::Buffer> buffer, ReadFile(path));

    std::vector<double> timings;
    timings.reserve(static_cast<std::size_t>(iterations));

    for (int iteration = 0; iteration < iterations; ++iteration)
    {
        auto started = std::chrono::steady_clock::now();
        ARROW_ASSIGN_OR_RAISE(int64_t actualRows, ReadRows(buffer));
        auto stopped = std::chrono::steady_clock::now();

        if (actualRows != rows)
        {
            return arrow::Status::Invalid("Fixture ", path.string(), " decoded ", actualRows,
                                         " rows, expected ", rows);
        }

        timings.push_back(std::chrono::duration<double, std::milli>(stopped - started).count());
    }

    double total = std::accumulate(timings.begin(), timings.end(), 0.0);
    double mean = total / static_cast<double>(timings.size());

    std::cout << rows << ',' << codec << ",read compressed IPC stream,arrow-cpp," << iterations << ',' << mean << '\n';
    return arrow::Status::OK();
}

arrow::Status BenchmarkWriteStream(int64_t rows, std::string_view codec, int iterations)
{
    ARROW_ASSIGN_OR_RAISE(std::shared_ptr<arrow::RecordBatch> batch, CreateRecordBatch(rows));

    std::vector<double> timings;
    timings.reserve(static_cast<std::size_t>(iterations));

    for (int iteration = 0; iteration < iterations; ++iteration)
    {
        auto started = std::chrono::steady_clock::now();
        ARROW_ASSIGN_OR_RAISE(std::shared_ptr<arrow::Buffer> buffer, WriteCompressedIpcStream(batch, codec));
        auto stopped = std::chrono::steady_clock::now();

        if (buffer->size() <= 0)
        {
            return arrow::Status::Invalid("C++ IPC writer returned an empty stream");
        }

        timings.push_back(std::chrono::duration<double, std::milli>(stopped - started).count());
    }

    double total = std::accumulate(timings.begin(), timings.end(), 0.0);
    double mean = total / static_cast<double>(timings.size());

    std::cout << rows << ',' << codec << ",write compressed IPC stream,arrow-cpp," << iterations << ',' << mean << '\n';
    return arrow::Status::OK();
}

void PrintUsage(const char* executable)
{
    std::cerr << "Usage:\n"
              << "  " << executable << " --validate --fixtures <dir>\n"
              << "  " << executable << " --benchmark --fixtures <dir> [--iterations <n>]\n";
}

arrow::Result<Options> ParseArgs(std::span<char*> args)
{
    Options options;

    for (std::size_t i = 1; i < args.size(); ++i)
    {
        std::string_view arg(args[i]);
        if (arg == "--validate")
        {
            options.validate = true;
        }
        else if (arg == "--benchmark")
        {
            options.benchmark = true;
        }
        else if (arg == "--fixtures" && i + 1 < args.size())
        {
            options.fixtures = args[++i];
        }
        else if (arg == "--iterations" && i + 1 < args.size())
        {
            try
            {
                options.iterations = std::stoi(args[++i]);
            }
            catch (const std::exception&)
            {
                return arrow::Status::Invalid("--iterations must be an integer");
            }
        }
        else
        {
            return arrow::Status::Invalid("Unknown or incomplete argument: ", std::string(arg));
        }
    }

    if (options.validate == options.benchmark)
    {
        return arrow::Status::Invalid("Choose exactly one mode: --validate or --benchmark");
    }

    if (options.fixtures.empty())
    {
        return arrow::Status::Invalid("Missing --fixtures <dir>");
    }

    if (options.iterations <= 0)
    {
        return arrow::Status::Invalid("--iterations must be positive");
    }

    return options;
}

arrow::Status Run(const Options& options)
{
    if (options.benchmark)
    {
        std::cout << "rows,codec,path,backend,iterations,mean_ms\n";
    }

    for (int64_t rows : kRowCounts)
    {
        for (std::string_view codec : kCodecs)
        {
            if (options.validate)
            {
                ARROW_RETURN_NOT_OK(ValidateFixture(options.fixtures, rows, codec));
            }
            else
            {
                ARROW_RETURN_NOT_OK(BenchmarkReadFixture(options.fixtures, rows, codec, options.iterations));
                ARROW_RETURN_NOT_OK(BenchmarkWriteStream(rows, codec, options.iterations));
            }
        }
    }

    return arrow::Status::OK();
}
}

int main(int argc, char** argv)
{
    arrow::Result<Options> parsed = ParseArgs(std::span<char*>(argv, static_cast<std::size_t>(argc)));
    if (!parsed.ok())
    {
        std::cerr << parsed.status().ToString() << '\n';
        PrintUsage(argv[0]);
        return 2;
    }

    arrow::Status status = Run(*parsed);
    if (!status.ok())
    {
        std::cerr << status.ToString() << '\n';
        return 1;
    }

    return 0;
}
