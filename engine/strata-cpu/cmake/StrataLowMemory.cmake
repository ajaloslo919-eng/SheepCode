# Dense CPU backend of the SheepCode Strata fork. The GPU/MoE backend is unchanged.
# Use the SAME pinned ggml dependency as upstream Strata, with its dense-model API.
set(STRATA_GGML_DIR "" CACHE PATH "Pinned llama.cpp checkout (3cf03257f219afbe7334045ff7c6a06ac68c627d)")
option(STRATA_CPU_STATIC_LINUX "Link a self-contained Linux executable for compatibility verification" OFF)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
foreach(flag GGML_NATIVE GGML_CPU_ALL_VARIANTS GGML_BACKEND_DL GGML_CUDA GGML_HIP GGML_VULKAN GGML_METAL GGML_BLAS GGML_OPENMP GGML_LLAMAFILE
             GGML_SSE42 GGML_AVX GGML_AVX2 GGML_FMA GGML_F16C GGML_BMI2 GGML_AVX_VNNI GGML_AVX512 GGML_AVX512_VBMI GGML_AVX512_VNNI
             GGML_AVX512_BF16 GGML_AMX_TILE GGML_AMX_INT8 GGML_AMX_BF16
             LLAMA_BUILD_COMMON LLAMA_BUILD_TESTS LLAMA_BUILD_EXAMPLES LLAMA_BUILD_TOOLS LLAMA_BUILD_SERVER LLAMA_BUILD_MTMD LLAMA_CURL)
    set(${flag} OFF CACHE BOOL "" FORCE)
endforeach()
set(GGML_STATIC ON CACHE BOOL "" FORCE)
if(STRATA_GGML_DIR)
    set(cpu_dependency "${STRATA_GGML_DIR}")
else()
    include(FetchContent)
    FetchContent_Declare(strata_cpu_dependency
        GIT_REPOSITORY https://github.com/ggml-org/llama.cpp.git
        GIT_TAG 3cf03257f219afbe7334045ff7c6a06ac68c627d GIT_SHALLOW FALSE)
    FetchContent_GetProperties(strata_cpu_dependency)
    if(NOT strata_cpu_dependency_POPULATED)
        FetchContent_Populate(strata_cpu_dependency)
    endif()
    set(cpu_dependency "${strata_cpu_dependency_SOURCE_DIR}")
endif()
add_subdirectory("${cpu_dependency}" "${CMAKE_BINARY_DIR}/dense-cpu" EXCLUDE_FROM_ALL)
add_executable(strata-cpu "${CMAKE_CURRENT_LIST_DIR}/../src/cpu/server.cpp" "${cpu_dependency}/vendor/cpp-httplib/httplib.cpp")
target_include_directories(strata-cpu PRIVATE "${cpu_dependency}/vendor")
target_link_libraries(strata-cpu PRIVATE llama)
target_compile_definitions(strata-cpu PRIVATE CPPHTTPLIB_THREAD_POOL_COUNT=3 CPPHTTPLIB_PAYLOAD_MAX_LENGTH=2097152 CPPHTTPLIB_KEEPALIVE_MAX_COUNT=8)
if(MSVC)
    target_compile_options(strata-cpu PRIVATE /O2 /utf-8 /EHsc)
    target_link_libraries(strata-cpu PRIVATE ws2_32 psapi)
else()
    # Never inherit the build host's ISA: even Core 2 / older Celeron x64 works.
    target_compile_options(strata-cpu PRIVATE -O2 -march=x86-64 -mno-avx -mno-avx2 -mno-sse4.1 -mno-sse4.2)
    if(STRATA_CPU_STATIC_LINUX)
        target_link_options(strata-cpu PRIVATE -static)
    endif()
endif()
message(STATUS "Strata low-memory CPU: x64 / SSE2 baseline; no GPU, AVX, Python, OpenMP or locked model pages")
