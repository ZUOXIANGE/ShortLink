#!/bin/sh
set -eu

: "${API_BASE_URL:?API_BASE_URL must be the browser-accessible absolute API URL}"
case "${API_BASE_URL}" in
  http://*|https://*) ;;
  *)
    echo "API_BASE_URL must start with http:// or https://" >&2
    exit 1
    ;;
esac

export API_BASE_URL
envsubst '${API_BASE_URL}' \
  < /usr/share/nginx/html/appsettings.template.json \
  > /usr/share/nginx/html/appsettings.json

# 安全响应头在运行时生成：CSP 的 connect-src 必须包含浏览器实际访问的 API 源，
# 而它由部署方通过 API_BASE_URL 注入，不能烧死在镜像里。
api_origin="${API_BASE_URL%/}"
mkdir -p /etc/nginx/snippets
cat > /etc/nginx/snippets/security-headers.conf <<EOF
add_header X-Content-Type-Options "nosniff" always;
add_header X-Frame-Options "DENY" always;
add_header Referrer-Policy "no-referrer" always;
add_header Permissions-Policy "camera=(), microphone=(), geolocation=(), payment=()" always;
add_header Content-Security-Policy "default-src 'self'; script-src 'self' 'unsafe-eval' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self' ${api_origin}; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'; worker-src 'self' blob:" always;
EOF
