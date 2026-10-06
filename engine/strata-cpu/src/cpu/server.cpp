// SheepCode Strata fork: small dense models on CPU, including x64 Celeron without AVX.
// MIT. ggml / llama dense graph, vocabulary and grammar are MIT dependencies; this
// server owns admission, bounded prefill, prefix reuse, transport and cancellation.
#include "llama.h"
#include "ggml-cpu.h"
#include "cpp-httplib/httplib.h"
#include "nlohmann/json.hpp"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#ifdef _WIN32
#include <windows.h>
#include <psapi.h>
#else
#include <sys/resource.h>
#include <unistd.h>
#endif

using json = nlohmann::ordered_json;
using clock_type = std::chrono::steady_clock;
extern "C" int strata_q8_sse2_verify(void);
constexpr uint64_t mib = 1048576;
const char *json_grammar = R"gbnf(
root ::= object
value ::= object | array | string | number | ("true" | "false" | "null") ws
object ::= "{" ws (string ":" ws value ("," ws string ":" ws value)*)? "}" ws
array ::= "[" ws (value ("," ws value)*)? "]" ws
string ::= "\"" ([^"\\\x7F\x00-\x1F] | "\\" (["\\bfnrt] | "u" [0-9a-fA-F]{4}))* "\"" ws
number ::= ("-"? ([0-9] | [1-9] [0-9]{0,15})) ("." [0-9]+)? ([eE] [-+]? [0-9] [1-9]{0,15})? ws
ws ::= | " " | "\n" [ \t]{0,20}
)gbnf";

struct options {
    std::string model, alias = "strata-cpu";
    int port = 8088, context = 4096, threads = 2, memory_mib = 1536;
};
static int integer(const std::string &s, int low, int high) {
    size_t end = 0; int value = std::stoi(s, &end);
    if (end != s.size() || value < low || value > high) throw std::runtime_error("integer outside allowed range: " + s);
    return value;
}
static json memory_status() {
#ifdef _WIN32
    PROCESS_MEMORY_COUNTERS_EX counters{}; counters.cb = sizeof(counters);
    if (!GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS *>(&counters), sizeof(counters))) throw std::runtime_error("cannot read process memory");
    MEMORYSTATUSEX system{}; system.dwLength = sizeof(system);
    if (!GlobalMemoryStatusEx(&system)) throw std::runtime_error("cannot read system memory");
    return {{"resident_bytes", counters.WorkingSetSize}, {"peak_resident_bytes", counters.PeakWorkingSetSize}, {"private_bytes", counters.PrivateUsage},
            {"physical_total_bytes", system.ullTotalPhys}, {"physical_available_bytes", system.ullAvailPhys}};
#else
    struct rusage usage{}; getrusage(RUSAGE_SELF, &usage);
    std::ifstream stat("/proc/self/statm"); uint64_t total = 0, resident = 0; stat >> total >> resident;
    return {{"resident_bytes", resident * uint64_t(sysconf(_SC_PAGESIZE))}, {"peak_resident_bytes", uint64_t(usage.ru_maxrss) * 1024},
            {"virtual_bytes", total * uint64_t(sysconf(_SC_PAGESIZE))}, {"physical_total_bytes", uint64_t(sysconf(_SC_PHYS_PAGES)) * uint64_t(sysconf(_SC_PAGESIZE))}};
#endif
}
static void memory_limit(uint64_t bytes) {
#ifdef _WIN32
    // Keep the handle for the process lifetime. Nested jobs are supported since Win8.
    static HANDLE job = CreateJobObjectW(nullptr, nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY;
    limits.ProcessMemoryLimit = SIZE_T(bytes);
    if (!job || !SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) || !AssignProcessToJobObject(job, GetCurrentProcess()))
        throw std::runtime_error("cannot enforce engine memory limit");
#else
    struct rlimit limit{rlim_t(bytes), rlim_t(bytes)};
    if (setrlimit(RLIMIT_AS, &limit)) throw std::runtime_error("cannot enforce engine address-space limit");
#endif
}
static void error(httplib::Response &res, int status, const std::string &message, const std::string &code = "invalid_request") {
    res.status = status; res.set_content(json{{"error", {{"message", message}, {"type", code}, {"code", code}}}}.dump(), "application/json");
}
static std::string template_prompt(const json &messages) {
    if (!messages.is_array() || messages.empty() || messages.size() > 64) throw std::runtime_error("messages must contain 1..64 text messages");
    std::string prompt;
    for (const auto &message : messages) {
        auto role = message.at("role").get<std::string>();
        if (role != "system" && role != "user" && role != "assistant" && role != "tool") throw std::runtime_error("unsupported role");
        const auto &content = message.at("content");
        if (!content.is_string()) throw std::runtime_error("this CPU profile accepts text only");
        prompt += "<|im_start|>" + role + "\n";
        // Keep the non-thinking prefix in earlier assistant turns too. Otherwise
        // the next tool step invalidates the KV cache before the generated action.
        if (role == "assistant") prompt += "<think>\n\n</think>\n\n";
        prompt += content.get<std::string>() + "<|im_end|>\n";
    }
    // Qwen3's documented non-thinking assistant prefix: avoid an unbounded hidden
    // reasoning stream on weak CPUs. A GUI reasoning setting cannot override it.
    prompt += "<|im_start|>assistant\n<think>\n\n</think>\n\n";
    return prompt;
}

class engine {
    options config;
    std::unique_ptr<llama_model, decltype(&llama_model_free)> model{nullptr, llama_model_free};
    std::unique_ptr<llama_context, decltype(&llama_free)> context{nullptr, llama_free};
    const llama_vocab *vocab = nullptr;
    std::mutex generation;
    std::atomic<bool> cancelled{false};
    clock_type::time_point deadline{};
    const httplib::Request *active_request = nullptr;
    std::vector<llama_token> cached;
    static bool abort(void *data) { return static_cast<engine *>(data)->should_abort(); }
    bool should_abort() const {
        return cancelled.load() || clock_type::now() > deadline || (active_request && active_request->is_connection_closed());
    }
    void decode(llama_token *tokens, int count) {
        auto batch = llama_batch_get_one(tokens, count);
        const int result = llama_decode(context.get(), batch);
        if (result != 0) {
            cached.clear(); llama_memory_clear(llama_get_memory(context.get()), true);
            throw std::runtime_error(result == 2 || should_abort() ? "generation cancelled" : "CPU decode failed: " + std::to_string(result));
        }
    }
public:
    explicit engine(options value) : config(std::move(value)) {
        const uint64_t size = std::filesystem::file_size(std::filesystem::u8path(config.model));
        if (size + 512 * mib > uint64_t(config.memory_mib) * mib) throw std::runtime_error("model exceeds the CPU profile memory budget; choose the integrated 0.6B model");
        memory_limit(uint64_t(config.memory_mib) * mib);
        llama_backend_init();
        if (!strata_q8_sse2_verify()) throw std::runtime_error("SSE2 Q8 correctness check failed");
        auto mp = llama_model_default_params(); mp.n_gpu_layers = 0; mp.load_mode = LLAMA_LOAD_MODE_MMAP;
        mp.use_extra_bufts = false; // No duplicate repacked weight buffers.
        model.reset(llama_model_load_from_file(config.model.c_str(), mp));
        if (!model) throw std::runtime_error("cannot load GGUF");
        char arch[64]{}; llama_model_meta_val_str(model.get(), "general.architecture", arch, sizeof(arch));
        if (std::string(arch) != "qwen3" || llama_model_n_params(model.get()) > 1100000000ULL) throw std::runtime_error("low-memory profile requires a dense Qwen3 model of at most 1.1B parameters");
        vocab = llama_model_get_vocab(model.get());
        auto cp = llama_context_default_params(); cp.n_ctx = config.context; cp.n_batch = 64; cp.n_ubatch = 32; cp.n_seq_max = 1;
        cp.n_threads = config.threads; cp.n_threads_batch = config.threads;
        cp.type_k = GGML_TYPE_F16; cp.type_v = GGML_TYPE_F16; cp.offload_kqv = false; cp.op_offload = false;
        cp.flash_attn_type = LLAMA_FLASH_ATTN_TYPE_DISABLED;
        cp.abort_callback = abort; cp.abort_callback_data = this;
        // Abort only becomes enabled once a request establishes its deadline.
        deadline = clock_type::time_point::max();
        context.reset(llama_init_from_model(model.get(), cp));
        if (!context) throw std::runtime_error("cannot allocate bounded CPU context");
        std::cerr << "STRATA_CPU_READY " << health().dump() << '\n';
    }
    std::vector<llama_token> tokenize(const std::string &text, bool special = true) const {
        int count = llama_tokenize(vocab, text.data(), int32_t(text.size()), nullptr, 0, special, true);
        if (count == 0) return {};
        if (count > 0) throw std::runtime_error("unexpected tokenizer capacity result");
        std::vector<llama_token> tokens(size_t(-count));
        int written = llama_tokenize(vocab, text.data(), int32_t(text.size()), tokens.data(), int32_t(tokens.size()), special, true);
        if (written < 0) throw std::runtime_error("tokenizer failed");
        tokens.resize(size_t(written)); return tokens;
    }
    json health() const {
        return {{"status", "ok"}, {"service", "strata"}, {"backend", "dense-cpu"}, {"loaded", true}, {"model", config.alias},
                {"max_context", config.context}, {"threads", config.threads}, {"memory_limit_mib", config.memory_mib},
                {"isa_floor", "x86-64/SSE2"}, {"compiled_avx", ggml_cpu_has_avx()}, {"compiled_avx2", ggml_cpu_has_avx2()},
                {"q8_kernel", "SSE2"}, {"q8_kernel_verified", true}, {"prefix_cache", true},
                {"reasoning", "none"}, {"images", false}, {"memory", memory_status()}, {"dependency", "llama.cpp/ggml 3cf03257f219afbe7334045ff7c6a06ac68c627d"}};
    }
    void cancel() { cancelled.store(true); }
    json complete(const httplib::Request &req, const json &body) {
        std::unique_lock lock(generation, std::try_to_lock);
        if (!lock.owns_lock()) throw std::runtime_error("busy: only one generation fits this profile");
        if (body.value("stream", false)) throw std::runtime_error("streaming is unavailable in this profile");
        const int limit = body.value("max_tokens", 512);
        if (limit < 1 || limit > 1536) throw std::runtime_error("max_tokens must be 1..1536");
        const auto prompt = template_prompt(body.at("messages")); const auto tokens = tokenize(prompt);
        if (tokens.size() + size_t(limit) > size_t(config.context)) throw std::length_error("request (" + std::to_string(tokens.size()) + " tokens) plus output exceeds context (" + std::to_string(config.context) + " tokens)");
        const auto started = clock_type::now(); cancelled.store(false); active_request = &req; deadline = started + std::chrono::minutes(20);
        struct reset_request { engine &self; ~reset_request() { self.active_request = nullptr; self.deadline = clock_type::time_point::max(); } } reset{*this};
        size_t prefix = 0;
        while (prefix < cached.size() && prefix < tokens.size() && cached[prefix] == tokens[prefix]) ++prefix;
        // Re-evaluate the last token so its logits are always current, even for an identical prompt.
        if (prefix == tokens.size() && prefix) --prefix;
        if (!llama_memory_seq_rm(llama_get_memory(context.get()), 0, llama_pos(prefix), -1)) { prefix = 0; llama_memory_clear(llama_get_memory(context.get()), true); }
        cached.resize(prefix);
        for (size_t i = prefix; i < tokens.size();) {
            if (should_abort()) throw std::runtime_error("generation cancelled");
            const int count = int(std::min(size_t(64), tokens.size() - i));
            decode(const_cast<llama_token *>(tokens.data() + i), count);
            cached.insert(cached.end(), tokens.begin() + i, tokens.begin() + i + count); i += count;
        }
        const auto prefill_end = clock_type::now();
        using sampler_ptr = std::unique_ptr<llama_sampler, decltype(&llama_sampler_free)>;
        sampler_ptr sampler(llama_sampler_chain_init(llama_sampler_chain_default_params()), llama_sampler_free);
        if (body.contains("response_format") && body.at("response_format").value("type", "") == "json_object") {
            const auto grammar_text = body.at("response_format").value("grammar", std::string(json_grammar));
            if (grammar_text.empty() || grammar_text.size() > 32768) throw std::runtime_error("JSON grammar exceeds the bounded protocol size");
            auto grammar = llama_sampler_init_grammar(vocab, grammar_text.c_str(), "root");
            if (!grammar) throw std::runtime_error("invalid JSON grammar");
            llama_sampler_chain_add(sampler.get(), grammar);
        }
        llama_sampler_chain_add(sampler.get(), llama_sampler_init_top_k(20));
        const float temperature = std::clamp(body.value("temperature", 0.15f), 0.0f, 1.5f);
        if (!std::isfinite(temperature)) throw std::runtime_error("invalid temperature");
        llama_sampler_chain_add(sampler.get(), llama_sampler_init_temp(temperature));
        llama_sampler_chain_add(sampler.get(), llama_sampler_init_dist(42));
        std::string output, finish = "length"; int generated = 0;
        for (; generated < limit; ++generated) {
            if (should_abort()) throw std::runtime_error("generation cancelled");
            auto token = llama_sampler_sample(sampler.get(), context.get(), -1);
            if (llama_vocab_is_eog(vocab, token)) { finish = "stop"; break; }
            char piece_buffer[256]; int count = llama_token_to_piece(vocab, token, piece_buffer, sizeof(piece_buffer), 0, false);
            if (count >= 0) output.append(piece_buffer, size_t(count));
            else { std::string piece(size_t(-count), '\0'); count = llama_token_to_piece(vocab, token, piece.data(), int32_t(piece.size()), 0, false); if (count < 0) throw std::runtime_error("token piece failed"); output += piece; }
            decode(&token, 1); cached.push_back(token);
        }
        const double prefill_seconds = std::chrono::duration<double>(prefill_end - started).count();
        const double decode_seconds = std::chrono::duration<double>(clock_type::now() - prefill_end).count();
        return {{"id", "strata-cpu-local"}, {"object", "chat.completion"}, {"model", config.alias},
                {"choices", json::array({{{"index", 0}, {"message", {{"role", "assistant"}, {"content", output}}}, {"finish_reason", finish}}})},
                {"usage", {{"prompt_tokens", tokens.size()}, {"completion_tokens", generated}, {"total_tokens", tokens.size() + generated}}},
                {"strata", {{"cached_prompt_tokens", prefix}, {"evaluated_prompt_tokens", tokens.size() - prefix}, {"q8_kernel", "SSE2"}, {"q8_kernel_verified", true},
                    {"prefill_seconds", prefill_seconds}, {"decode_seconds", decode_seconds}, {"action_schema", body.contains("response_format") && body.at("response_format").contains("grammar")}, {"memory", memory_status()},
                    {"scope", "Active integrated Strata CPU component; excludes SheepCode tools, GUI, voice and playback."}}}};
    }
};

int main(int argc, char **argv) {
    try {
        options config;
        for (int i = 1; i < argc; ++i) {
            std::string key = argv[i];
            if (key == "--help") { std::cout << "Strata CPU: --model GGUF --alias ID --port 8088 --ctx-size 4096 --threads 2 --memory-mib 1536\nLoopback only; dense Qwen3 <=1.1B; no GPU/Python/AVX.\n"; return 0; }
            if (i + 1 == argc) throw std::runtime_error("missing value for " + key);
            std::string value = argv[++i];
            if (key == "--model") config.model = value;
            else if (key == "--alias") config.alias = value;
            else if (key == "--port") config.port = integer(value, 1024, 65535);
            else if (key == "--ctx-size") config.context = integer(value, 2048, 4096);
            else if (key == "--threads") config.threads = integer(value, 1, 4);
            else if (key == "--memory-mib") config.memory_mib = integer(value, 1280, 2048);
            else throw std::runtime_error("unknown option: " + key);
        }
        if (config.model.empty()) throw std::runtime_error("--model is required");
        engine cpu(config); httplib::Server server;
        server.new_task_queue = [] { return new httplib::ThreadPool(3, 3, 8); };
        server.set_payload_max_length(2097152); server.set_read_timeout(10); server.set_write_timeout(10);
        server.Get("/health", [&](const auto &, auto &res) { res.set_content(cpu.health().dump(), "application/json"); });
        server.Get("/v1/models", [&](const auto &, auto &res) { res.set_content(json{{"object", "list"}, {"data", json::array({{{"id", config.alias}, {"object", "model"}, {"owned_by", "strata"}}})}}.dump(), "application/json"); });
        server.Post("/apply-template", [](const auto &req, auto &res) {
            try { res.set_content(json{{"prompt", template_prompt(json::parse(req.body).at("messages"))}}.dump(), "application/json"); }
            catch (const std::exception &e) { error(res, 400, e.what()); }
        });
        server.Post("/tokenize", [&](const auto &req, auto &res) {
            try { auto body = json::parse(req.body); res.set_content(json{{"tokens", cpu.tokenize(body.at("content").template get<std::string>(), body.value("add_special", true))}}.dump(), "application/json"); }
            catch (const std::exception &e) { error(res, 400, e.what()); }
        });
        server.Post("/prompt-count", [&](const auto &req, auto &res) {
            try { res.set_content(json{{"count", cpu.tokenize(template_prompt(json::parse(req.body).at("messages"))).size()}}.dump(), "application/json"); }
            catch (const std::exception &e) { error(res, 400, e.what()); }
        });
        server.Post("/cancel", [&](const auto &, auto &res) { cpu.cancel(); res.set_content("{\"cancelled\":true}", "application/json"); });
        server.Post("/v1/chat/completions", [&](const auto &req, auto &res) {
            try { res.set_content(cpu.complete(req, json::parse(req.body)).dump(), "application/json"); }
            catch (const std::length_error &e) { error(res, 400, e.what(), "exceed_context_size_error"); }
            catch (const std::exception &e) { error(res, 400, e.what()); }
        });
        if (!server.listen("127.0.0.1", config.port)) throw std::runtime_error("cannot bind private loopback port");
        return 0;
    } catch (const std::exception &e) { std::cerr << "STRATA_CPU_ERROR " << e.what() << '\n'; return 1; }
}
