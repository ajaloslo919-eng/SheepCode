#!/bin/sh
# Compatibility of the installed SheepCode CPU component. No model comparison.
set -eu
task_root=/mnt/c/Users/sk/Desktop/plugin/sheepcode-work
task_source="$task_root/SheepCode-github/engine/strata-cpu"
task_installed="$task_root/checks/installed-celeron"
task_vm="$task_root/checks/strata-cpu-vm"
mkdir -p "$task_vm/root/bin" "$task_vm/root/proc" "$task_vm/root/sys" "$task_vm/root/dev" "$task_vm/root/tmp"
cmake -S "$task_source" -B "$task_source/build-linux" -G Ninja -DCMAKE_BUILD_TYPE=Release -DSTRATA_GGML_DIR=/mnt/e/SheepGPTAI/engines/strata/development/llama-src -DSTRATA_CPU_STATIC_LINUX=ON > "$task_vm/build-linux.log" 2>&1
cmake --build "$task_source/build-linux" --target strata-cpu -j 4 >> "$task_vm/build-linux.log" 2>&1
cp /usr/bin/busybox "$task_vm/root/bin/busybox"
cp "$task_source/build-linux/strata-cpu" "$task_vm/root/strata-cpu"
qemu-x86_64 -cpu Conroe "$task_vm/root/strata-cpu" --help > "$task_vm/user-mode-isa.txt"
qemu-x86_64 -cpu Conroe "$task_vm/root/bin/busybox" true
cp "$task_installed/models/qwen3-0.6b/Qwen3-0.6B-Q8_0.gguf" "$task_vm/root/model.gguf"
cp "$task_installed/state/engine.json" "$task_vm/root/installed-profile.json"
ln -sf busybox "$task_vm/root/bin/sh"
cat > "$task_vm/root/request.json" <<'EOF'
{"model":"qwen3-0.6b","messages":[{"role":"system","content":"Responde solo un objeto JSON con la clave respuesta."},{"role":"user","content":"Escribe hola Sheep en respuesta."}],"max_tokens":48,"temperature":0,"response_format":{"type":"json_object"}}
EOF
cat > "$task_vm/root/init" <<'EOF'
#!/bin/sh
export PATH=/bin
/bin/busybox --install -s /bin
mount -t proc proc /proc
mount -t sysfs sysfs /sys
mount -t devtmpfs devtmpfs /dev
ifconfig lo 127.0.0.1 up
echo STRATA_VM_HARDWARE_BEGIN
cat /proc/cpuinfo
cat /proc/meminfo
cat /installed-profile.json
echo STRATA_VM_HARDWARE_END
/strata-cpu --model /model.gguf --alias qwen3-0.6b --threads 2 --ctx-size 4096 --memory-mib 1536 --port 8088 > /tmp/engine.log 2>&1 &
task_engine=$!
task_count=0
while ! wget -q -O /tmp/health.json http://127.0.0.1:8088/health; do
    if ! kill -0 "$task_engine" 2>/dev/null; then echo STRATA_VM_FAILED; cat /tmp/engine.log; poweroff -f; fi
    task_count=$((task_count + 1))
    if [ "$task_count" -gt 1800 ]; then echo STRATA_VM_TIMEOUT; cat /tmp/engine.log; poweroff -f; fi
    sleep 1
done
echo STRATA_VM_HEALTH_BEGIN
cat /tmp/health.json
echo STRATA_VM_HEALTH_END
if wget -q -T 1800 -O /tmp/completion.json --header='Content-Type: application/json' --post-file=/request.json http://127.0.0.1:8088/v1/chat/completions; then
    echo STRATA_VM_COMPLETION_BEGIN
    cat /tmp/completion.json
    echo STRATA_VM_COMPLETION_END
    echo STRATA_VM_MEMORY_BEGIN
    cat /proc/meminfo
    cat /proc/$task_engine/status
    echo STRATA_VM_MEMORY_END
    echo STRATA_VM_FINISHED
else
    echo STRATA_VM_FAILED
fi
cat /tmp/engine.log
poweroff -f
EOF
chmod +x "$task_vm/root/init" "$task_vm/root/strata-cpu" "$task_vm/root/bin/busybox"
(cd "$task_vm/root" && find . -print0 | cpio --null -o --format=newc 2>/dev/null | gzip -1 > "$task_vm/initramfs.gz")
# Existing Microsoft WSL Linux kernel; no guest image or user disk is modified.
qemu-system-x86_64 -machine pc -accel tcg,thread=multi -cpu Conroe -smp 2 -m 4096 -display none -monitor none -serial stdio -no-reboot \
    -kernel '/mnt/c/Program Files/WSL/tools/kernel' -initrd "$task_vm/initramfs.gz" \
    -append 'console=ttyS0 rdinit=/init panic=-1 quiet' -nic none > "$task_vm/serial.log" 2>&1
grep -q STRATA_VM_FINISHED "$task_vm/serial.log"
printf 'Strata CPU completed in 4096 MiB / two virtual CPUs / Conroe ISA without AVX. See %s\n' "$task_vm/serial.log"
