cmake_minimum_required(VERSION 3.25)

project(arrow_cpp_ipc_benchmark LANGUAGES CXX)

find_package(Arrow REQUIRED)
find_package(Threads REQUIRED)

add_executable(arrow_cpp_ipc_benchmark arrow_ipc_cpp_benchmark.cc)
target_compile_features(arrow_cpp_ipc_benchmark PRIVATE cxx_std_20)
target_link_libraries(arrow_cpp_ipc_benchmark PRIVATE Arrow::arrow_shared Threads::Threads)

add_library(arrow_cpp_ipc_shim SHARED arrow_ipc_cpp_shim.cc)
target_compile_features(arrow_cpp_ipc_shim PRIVATE cxx_std_20)
target_link_libraries(arrow_cpp_ipc_shim PRIVATE Arrow::arrow_shared Threads::Threads)
