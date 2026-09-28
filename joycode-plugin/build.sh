#!/usr/bin/env bash
# 构建 JoyCode 插件发行包：编译 → 组装 plugins/joycode/ → 生成可安装目录
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
project="$script_dir/src/JoyCode/JoyCode.csproj"
output="$script_dir/artifacts/plugins/joycode"

# 产物目录非空时拒绝覆盖，避免混入旧文件
if [[ -d $output && -n $(find "$output" -mindepth 1 -maxdepth 1 -print -quit) ]]; then
    echo "输出目录非空: $output" >&2
    exit 1
fi

# 编译（禁用构建服务器，保证可重现）
dotnet build "$project" -c Release --disable-build-servers -m:1 \
    -p:UseSharedCompilation=false -p:ConcurrentBuild=false

# 组装发行目录：主 DLL + deps.json + plugin.json，排除宿主自带的 Router.Contracts
mkdir -p "$output"
bin_dir="$script_dir/src/JoyCode/bin/Release/net10.0"
for file in "$bin_dir"/*; do
    name=$(basename "$file")
    # 跳过 Router.Contracts（宿主提供，避免双主程序集歧义）和调试符号
    [[ $name == Router.Contracts.* ]] && continue
    [[ $name == *.pdb ]] && continue
    cp "$file" "$output/"
done

# plugin.json 是 C# 包的元数据（runtime 必须 dotnet、id 必须等于目录名）
cp "$script_dir/src/JoyCode/plugin.json" "$output/"

echo "构建完成: $output"
ls -la "$output"
