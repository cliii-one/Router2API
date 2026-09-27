#!/bin/bash
# =============================================================
# Router2API 组装脚本：把上游构建产物装进 fnOS 应用壳
# 仅供 GitHub Actions 调用（本机 Arm 性能不足，构建全部放在 CI 上）
#
# fnOS 应用包结构规范（fnpack 要求，已实测）：
#   包根目录/
#   ├── manifest              → 应用元数据
#   ├── cmd/                  → 9 个生命周期脚本（必须齐全，否则 fnpack 报错）
#   ├── config/               → privilege + resource
#   ├── wizard/               → 安装向导
#   ├── ICON.PNG/ICON_256.PNG → 图标
#   └── app/                  → ★ 应用运行内容：打包为 app.tgz，
#        安装后解压到 TRIM_APPDEST（即 /var/apps/Router2API/target/）
#
# 环境变量：
#   UPSTREAM_DIR  上游仓库根目录（已 checkout）
#   STAGE_DIR     组装输出目录
#   R2A_VERSION   上游版本号（写入 manifest）
#   R2A_ARCH      目标架构：arm64 或 amd64（默认当前机器架构）
# =============================================================
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UPSTREAM_DIR="${UPSTREAM_DIR:?请设置 UPSTREAM_DIR 为上游 Router2API 仓库根目录}"
STAGE_DIR="${STAGE_DIR:?请设置 STAGE_DIR 为组装输出目录}"
R2A_VERSION="${R2A_VERSION:-2.0.0}"
R2A_ARCH="${R2A_ARCH:-$(uname -m)}"

# 归一化架构名：.NET 用 x64 / arm64，Debian 用 amd64 / arm64
case "${R2A_ARCH}" in
    x86_64|amd64|x64) DOTNET_RID="linux-x64";   DEB_ARCH="amd64" ;;
    aarch64|arm64)    DOTNET_RID="linux-arm64"; DEB_ARCH="arm64" ;;
    *) echo "FATAL: 不支持的架构 ${R2A_ARCH}" >&2; exit 1 ;;
esac

APP_PKG="${STAGE_DIR}/Router2API"
APP_CONTENT="${APP_PKG}/app"

log() { echo "==> $*"; }

# -------------------------------------------------------------
# 1. 清理并创建组装目录
# -------------------------------------------------------------
log "[1/6] 清理并创建组装目录"
rm -rf "${STAGE_DIR}"
mkdir -p "${APP_PKG}" "${APP_CONTENT}"

# -------------------------------------------------------------
# 2. 复制应用壳（manifest/cmd/config/wizard/ui/图标）
# -------------------------------------------------------------
log "[2/6] 复制应用壳"
cp "${REPO_ROOT}/appshell/manifest" "${APP_PKG}/"
cp -r "${REPO_ROOT}/appshell/cmd" "${APP_PKG}/"
cp -r "${REPO_ROOT}/appshell/config" "${APP_PKG}/"
cp -r "${REPO_ROOT}/appshell/wizard" "${APP_PKG}/"
cp -r "${REPO_ROOT}/appshell/ui" "${APP_PKG}/"
# app/ui 也放一份：fnpack 打包 app.tgz 时要求该路径存在
cp -r "${REPO_ROOT}/appshell/ui" "${APP_CONTENT}/"
cp "${REPO_ROOT}/appshell/ICON.PNG" "${REPO_ROOT}/appshell/ICON_256.PNG" "${APP_PKG}/"

# 把实际版本号写进 manifest（应用壳里是占位值）
sed -i "s/^version  *=.*/version               = ${R2A_VERSION}/" "${APP_PKG}/manifest"

# -------------------------------------------------------------
# 3. 构建前端（Vue 3）
#    上游的 web 是独立 pnpm 工程，构建产物会被同步到
#    src/Router.Host/wwwroot，随后 dotnet publish 一并带上
# -------------------------------------------------------------
log "[3/6] 构建前端（pnpm）"
if [ ! -d "${UPSTREAM_DIR}/web" ]; then
    echo "FATAL: 上游目录缺少 web/，请检查 UPSTREAM_DIR" >&2
    exit 1
fi

pushd "${UPSTREAM_DIR}/web" >/dev/null
# corepack 会读取 package.json 里声明的 pnpm 版本，保证与上游一致
corepack enable 2>/dev/null || true
pnpm install --frozen-lockfile
pnpm build
popd >/dev/null

if [ ! -f "${UPSTREAM_DIR}/src/Router.Host/wwwroot/index.html" ]; then
    echo "FATAL: 前端构建产物未同步到 src/Router.Host/wwwroot（检查 pnpm build 输出）" >&2
    exit 1
fi
log "    前端构建完成"

# -------------------------------------------------------------
# 4. 发布 .NET 宿主（自包含，带运行时）
#    飞牛应用中心没有 dotnet 依赖包，所以必须自包含发布
# -------------------------------------------------------------
log "[4/6] 发布 .NET 宿主（RID=${DOTNET_RID}，自包含）"
pushd "${UPSTREAM_DIR}" >/dev/null
# TreatWarningsAsErrors=false：上游把警告当错误，用于约束自己的代码质量。
# 作为打包方，不该因为上游新增的一条分析器警告就整体构建失败，
# 代码质量由上游自己的 CI 把关，这里只关心能否产出可运行的程序。
dotnet publish src/Router.Host/Router.Host.csproj \
    -c Release \
    -r "${DOTNET_RID}" \
    --self-contained true \
    -o "${APP_CONTENT}/host" \
    -p:PublishSingleFile=false \
    -p:DebugType=none \
    -p:TreatWarningsAsErrors=false \
    -p:GenerateDocumentationFile=false
popd >/dev/null

# 自包含产物：优先 apphost（Router.Host 可执行文件），确保有执行权限
if [ ! -f "${APP_CONTENT}/host/Router.Host.dll" ]; then
    echo "FATAL: 未找到 Router.Host.dll，dotnet publish 失败" >&2
    exit 1
fi
if [ ! -x "${APP_CONTENT}/host/Router.Host" ] && [ -f "${APP_CONTENT}/host/Router.Host" ]; then
    chmod +x "${APP_CONTENT}/host/Router.Host"
fi
log "    宿主发布完成：$(du -sh "${APP_CONTENT}/host" | cut -f1)"

# -------------------------------------------------------------
# 5. 内置 Redis
#    飞牛应用中心没有 Redis 依赖包，而 Router2API 的代理池冷却
#    与任务锁强依赖 Redis（源码明确无内存回退），因此随包携带。
# -------------------------------------------------------------

# 固定使用 Debian bookworm 的 Redis 7.0.15。
#
# 为什么固定这个版本：
#   飞牛基于 Debian 12(bookworm)，GLIBC 2.36。
#   实测 7.0.15 最高需要 GLIBC_2.34 → 完全兼容 ✅
#   而 Debian 13(trixie) 编译的 8.x 需要 GLIBC_2.38 → 报错无法运行 ❌
#   所以不放任"取最新版"，直接钉死这个已验证可用的版本。
#
# 关于两个 Debian 包的细节（都是实测踩出来的）：
#   1. 实体二进制在 redis-tools 里，不在 redis-server 包里。
#      redis-server 包的 /usr/bin/redis-server 只是指向 redis-check-rdb 的软链。
#   2. liblzf.so.1 是 Redis 的 LZF 压缩依赖，飞牛系统本身不带，必须一起打包。
#
# Debian 池路径按包名首字母分子目录，redis 在 r/redis 下。
REDIS_VERSION="7.0.15-1~deb12u7"
LZF_VERSION="3.6-4+b4"
DEB_POOL="${DEB_POOL:-https://deb.debian.org/debian/pool/main}"

log "[5/6] 内置 Redis ${REDIS_VERSION}（${DEB_ARCH}）"
REDIS_DIR="${APP_CONTENT}/redis"
mkdir -p "${REDIS_DIR}/lib" "${REDIS_DIR}/tmp"

# 下载并解包一个 deb 到指定目录
fetch_deb() {
    local pool_path="$1" name="$2" out_dir="$3" label="$4"
    local url="${DEB_POOL}/${pool_path}/${name}"
    log "    ${label}: ${name}"
    mkdir -p "${out_dir}"
    curl -sfL --retry 3 --retry-delay 2 --max-time 300 -o "${out_dir}.deb" "${url}" \
        || { echo "FATAL: 下载失败 ${url}" >&2; exit 1; }
    dpkg-deb -x "${out_dir}.deb" "${out_dir}" \
        || { echo "FATAL: 解包失败 ${url}" >&2; exit 1; }
}

WORK_DEB="$(mktemp -d)"
fetch_deb "r/redis" "redis-tools_${REDIS_VERSION}_${DEB_ARCH}.deb" "${WORK_DEB}/x1" "redis-tools"
fetch_deb "libl/liblzf" "liblzf1_${LZF_VERSION}_${DEB_ARCH}.deb" "${WORK_DEB}/x2" "liblzf"

# redis-server 是 redis-check-rdb 的多调用别名（按 argv[0] 决定行为）
cp "${WORK_DEB}/x1/usr/bin/redis-check-rdb" "${REDIS_DIR}/redis-server"
cp "${WORK_DEB}/x1/usr/bin/redis-cli" "${REDIS_DIR}/redis-cli"
chmod 755 "${REDIS_DIR}/redis-server" "${REDIS_DIR}/redis-cli"
# liblzf 放到包内 lib 目录，启动脚本用 LD_LIBRARY_PATH 指向它
cp -P "${WORK_DEB}/x2/usr/lib/"*"/liblzf.so.1"* "${REDIS_DIR}/lib/"
rm -rf "${WORK_DEB}"

# 实测校验：真跑一次，确认在当前系统能执行（GLIBC 兼容）
if ! LD_LIBRARY_PATH="${REDIS_DIR}/lib" "${REDIS_DIR}/redis-server" --version >/dev/null 2>&1; then
    echo "FATAL: Redis 二进制无法执行，检查 GLIBC 兼容性" >&2
    LD_LIBRARY_PATH="${REDIS_DIR}/lib" "${REDIS_DIR}/redis-server" --version 2>&1 | head -3 >&2
    exit 1
fi
log "    Redis 就绪：$(LD_LIBRARY_PATH="${REDIS_DIR}/lib" "${REDIS_DIR}/redis-server" --version | head -1)"

# 保留来源与许可说明（Redis 7.0 起采用 RSALv2 / SSPLv1 双许可）
cat > "${REDIS_DIR}/README.txt" <<EOF
Redis 二进制随 Router2API 应用包一同分发，仅在本应用内使用。

来源：Debian bookworm 软件包，通过 dpkg-deb 提取
      - redis-tools_${REDIS_VERSION}_${DEB_ARCH}.deb
      - liblzf1_${LZF_VERSION}_${DEB_ARCH}.deb
用途：Router2API 宿主需要 Redis 提供插件共享状态、任务锁与代理池冷却策略。
说明：redis-server 由 redis-tools 的 redis-check-rdb 改名而来（多调用二进制）。
      lib/ 目录下的 liblzf.so.1 为 Redis 的压缩依赖，启动脚本会设置
      LD_LIBRARY_PATH 指向该目录。

版本：固定 7.0.15。飞牛基于 Debian 12（GLIBC 2.36），该版本最高需要 GLIBC_2.34
      可用；Debian 13(trixie) 编译的 8.x 需要 GLIBC_2.38，装上无法运行。

许可：Redis 采用 RSALv2 / SSPLv1 双许可（7.0 起）。
      完整条款见上游 https://redis.io/legal/licenses/ 。
EOF
# 插件目录：宿主按「程序目录/plugins」查找，启动脚本会把它软链到
# var/plugins 实现持久化。这里预置空目录并占用 .subscription。
mkdir -p "${APP_CONTENT}/host/plugins/.subscription"
# 数据目录占位（真实运行数据与 Config.json 都在 TRIM_PKGVAR）
mkdir -p "${APP_CONTENT}/data"

# -------------------------------------------------------------
# 6. 收尾：清理软链 + 统一权限 + 校验
# -------------------------------------------------------------
log "[6/6] 清理软链、统一权限并校验"

# 飞牛安装器会对 app.tgz 递归设置目录权限（ApplyPermission）。
# 软链（尤其自包含发布可能带出的 .so 软链）会干扰这一过程，
# 统一实体化：有效软链复制成真实文件，断链直接删除。
RESOLVED=0; BROKEN=0
while IFS= read -r link; do
    [ -z "${link}" ] && continue
    if [ -e "${link}" ]; then
        target=$(readlink -f "${link}")
        rm -f "${link}"
        cp -a "${target}" "${link}"
        RESOLVED=$((RESOLVED + 1))
    else
        rm -f "${link}"
        BROKEN=$((BROKEN + 1))
    fi
done < <(find "${APP_CONTENT}" -type l 2>/dev/null)
log "    实体化软链 ${RESOLVED} 个，删除断链 ${BROKEN} 个"

REMAIN=$(find "${APP_CONTENT}" -type l 2>/dev/null | wc -l)
if [ "${REMAIN}" -gt 0 ]; then
    echo "FATAL: 仍残留 ${REMAIN} 个软链，安装器可能报权限失败" >&2
    find "${APP_CONTENT}" -type l >&2
    exit 1
fi

# 权限：目录 755、普通文件 644，可执行文件显式 755。
# 显式设置而不是依赖 umask —— 实测在某些 NAS / CI 环境里 umask 是 077，
# 会让包内文件变成 600/700，安装器读不到；这里统一成可预期的权限。
find "${APP_CONTENT}" -type d -exec chmod 755 {} + 2>/dev/null || true
find "${APP_CONTENT}" -type f -exec chmod 644 {} + 2>/dev/null || true
chmod 755 "${APP_CONTENT}/host/Router.Host" 2>/dev/null || true
chmod 755 "${APP_CONTENT}/redis/redis-server" "${APP_CONTENT}/redis/redis-cli" 2>/dev/null || true
chmod 755 "${APP_CONTENT}/host/createdump" 2>/dev/null || true

# 包根目录的文件同样要规范权限（fpk 会把这些文件单独打包）
find "${APP_PKG}" -maxdepth 1 -type f -exec chmod 644 {} + 2>/dev/null || true
find "${APP_PKG}" -maxdepth 1 -type d -exec chmod 755 {} + 2>/dev/null || true
chmod 644 "${APP_PKG}/ICON.PNG" "${APP_PKG}/ICON_256.PNG" "${APP_PKG}/manifest" 2>/dev/null || true
chmod 755 "${APP_PKG}/cmd/"* 2>/dev/null || true
chmod 644 "${APP_PKG}/config/"* "${APP_PKG}/wizard/"* 2>/dev/null || true
find "${APP_PKG}/cmd" -type d -exec chmod 755 {} + 2>/dev/null || true
find "${APP_PKG}/config" "${APP_PKG}/wizard" -type d -exec chmod 755 {} + 2>/dev/null || true
# ui 目录（桌面入口配置与图标）
find "${APP_PKG}/ui" -type d -exec chmod 755 {} + 2>/dev/null || true
find "${APP_PKG}/ui" -type f -exec chmod 644 {} + 2>/dev/null || true

# ---- 最终校验 ----
fail=0
check_file() { [ -f "$1" ] || { echo "FATAL: 缺少 $1" >&2; fail=1; }; }
check_file "${APP_PKG}/manifest"
check_file "${APP_PKG}/ICON.PNG"
check_file "${APP_PKG}/ICON_256.PNG"
check_file "${APP_CONTENT}/host/Router.Host.dll"
check_file "${APP_CONTENT}/host/Router.Host"
check_file "${APP_CONTENT}/host/appsettings.json"
check_file "${APP_CONTENT}/host/wwwroot/index.html"
check_file "${APP_CONTENT}/redis/redis-server"
check_file "${APP_CONTENT}/redis/redis-cli"
check_file "${APP_CONTENT}/ui/config"
check_file "${APP_PKG}/wizard/install"
check_file "${APP_PKG}/config/privilege"
check_file "${APP_PKG}/config/resource"
[ -d "${APP_PKG}/cmd" ] || { echo "FATAL: 缺少 cmd/" >&2; fail=1; }
[ -d "${APP_CONTENT}/host/wwwroot" ] || { echo "FATAL: 缺少 host/wwwroot（管理后台前端）" >&2; fail=1; }
[ -d "${APP_CONTENT}/host/plugins" ] || { echo "FATAL: 缺少 host/plugins（插件目录）" >&2; fail=1; }

# Redis 必须真的能跑（缓存本机已验证过，这里做最终确认）
if [ "${fail}" -eq 0 ]; then
    if ! LD_LIBRARY_PATH="${APP_CONTENT}/redis/lib" "${APP_CONTENT}/redis/redis-server" --version >/dev/null 2>&1; then
        echo "FATAL: Redis 无法执行，检查 GLIBC 兼容性" >&2
        fail=1
    fi
fi

for s in main install_init install_callback upgrade_init upgrade_callback \
         uninstall_init uninstall_callback config_init config_callback; do
    [ -f "${APP_PKG}/cmd/${s}" ] || { echo "FATAL: 缺少 cmd/${s}" >&2; fail=1; }
done

[ "${fail}" -eq 0 ] || exit 1

echo
echo "==> 组装完成"
echo "    包目录 : ${APP_PKG}"
echo "    应用内容: ${APP_CONTENT}（$(du -sh "${APP_CONTENT}" | cut -f1)）"
echo "    目标架构: ${DOTNET_RID} / ${DEB_ARCH}"
echo "    版本号  : ${R2A_VERSION}"
