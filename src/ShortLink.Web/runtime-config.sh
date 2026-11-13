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
