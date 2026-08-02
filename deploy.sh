#!/usr/bin/env bash
# Deployment helper for WarehouseApp

set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="${COMPOSE_FILE:-$SCRIPT_DIR/docker-compose.yml}"
PROJECT_NAME="${PROJECT_NAME:-warehouse}"
CONTAINER_NAME="${CONTAINER_NAME:-warehouse-app}"
DOCKER_NETWORK="${DOCKER_NETWORK:-shared-network}"
APP_PORT="${APP_PORT:-9095}"
export APP_PORT
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:$APP_PORT/}"
HEALTH_ATTEMPTS="${HEALTH_ATTEMPTS:-30}"
HEALTH_INTERVAL="${HEALTH_INTERVAL:-2}"
DEPLOY_PULL="${DEPLOY_PULL:-1}"

if [[ -t 1 ]]; then
    RED='\033[0;31m'
    GREEN='\033[0;32m'
    YELLOW='\033[1;33m'
    BLUE='\033[0;34m'
    NC='\033[0m'
else
    RED=''
    GREEN=''
    YELLOW=''
    BLUE=''
    NC=''
fi

log()    { printf "%b[OK]%b %s\n" "$GREEN" "$NC" "$*"; }
warn()   { printf "%b[WARN]%b %s\n" "$YELLOW" "$NC" "$*"; }
error()  { printf "%b[ERROR]%b %s\n" "$RED" "$NC" "$*" >&2; exit 1; }
header() { printf "\n%b========== %s ==========%b\n" "$BLUE" "$*" "$NC"; }

compose() {
    docker compose -f "$COMPOSE_FILE" -p "$PROJECT_NAME" "$@"
}

usage() {
    cat <<EOF
Usage: ./deploy.sh [command]

Commands:
  deploy     Pull changes (when Git is available), build, start and health-check
  pull       Pull the current Git branch with fast-forward only
  build      Build the application image using Docker layer cache
  up         Start or recreate the application
  down       Stop and remove the application container
  restart    Restart the application container
  logs       Follow application logs
  status     Show Compose and container status
  health     Check http://SERVER_IP:${APP_PORT}
  help       Show this help

Environment overrides:
  DEPLOY_PULL=0          Skip Git pull during deploy
  APP_PORT=9095          Published application port
  HEALTH_ATTEMPTS=30     Number of health-check attempts
  HEALTH_INTERVAL=2      Seconds between health checks

Examples:
  ./deploy.sh deploy
  DEPLOY_PULL=0 ./deploy.sh deploy
  ./deploy.sh logs
  ./deploy.sh status
EOF
}

on_error() {
    local exit_code=$?
    printf "\n%bDeployment command failed (exit code %s).%b\n" "$RED" "$exit_code" "$NC" >&2
    if command -v docker >/dev/null 2>&1; then
        compose ps 2>/dev/null || true
        docker logs "$CONTAINER_NAME" --tail 50 2>/dev/null || true
    fi
    exit "$exit_code"
}
trap on_error ERR

check_prerequisites() {
    command -v docker >/dev/null 2>&1 || error "Docker is not installed."
    docker info >/dev/null 2>&1 || error "Docker daemon is unavailable or this user has no Docker access."
    docker compose version >/dev/null 2>&1 || error "Docker Compose v2 is not installed."
    command -v curl >/dev/null 2>&1 || error "curl is not installed."
    [[ -f "$COMPOSE_FILE" ]] || error "Compose file not found: $COMPOSE_FILE"
    compose config --quiet
}

ensure_network() {
    if ! docker network inspect "$DOCKER_NETWORK" >/dev/null 2>&1; then
        warn "Docker network '$DOCKER_NETWORK' does not exist; creating it."
        docker network create "$DOCKER_NETWORK" >/dev/null
        log "Docker network '$DOCKER_NETWORK' created."
    fi
}

pull_source() {
    if [[ ! -d "$SCRIPT_DIR/.git" ]]; then
        warn "This directory is not a Git repository; skipping pull."
        return 0
    fi

    command -v git >/dev/null 2>&1 || error "Git is not installed."

    if [[ -n "$(git -C "$SCRIPT_DIR" status --porcelain)" ]]; then
        error "The server working tree has local changes. Commit or stash them before pulling."
    fi

    local branch
    branch="$(git -C "$SCRIPT_DIR" symbolic-ref --quiet --short HEAD)" || \
        error "Git is in detached HEAD state. Check out a deployment branch first."

    header "Pulling branch: $branch"
    git -C "$SCRIPT_DIR" pull --ff-only
    log "Source code is up to date."
}

build_app() {
    header "Building application image"
    compose build
    log "Image built successfully."
}

start_app() {
    header "Starting application"
    ensure_network
    compose up -d --remove-orphans
    log "Application container started."
}

health_check() {
    header "Checking application health"

    local attempt
    local status
    for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
        status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
            --max-time 5 "$HEALTH_URL" 2>/dev/null || true)"

        if [[ "$status" =~ ^(2|3)[0-9][0-9]$ ]]; then
            log "Application is ready: $HEALTH_URL (HTTP $status)"
            return 0
        fi

        printf "Waiting for application (%s/%s, HTTP %s)...\n" \
            "$attempt" "$HEALTH_ATTEMPTS" "${status:-unavailable}"
        sleep "$HEALTH_INTERVAL"
    done

    docker logs "$CONTAINER_NAME" --tail 80 2>/dev/null || true
    error "Application did not become healthy at $HEALTH_URL."
}

deploy() {
    check_prerequisites

    if [[ "$DEPLOY_PULL" == "1" ]]; then
        pull_source
    else
        warn "Git pull skipped because DEPLOY_PULL=$DEPLOY_PULL."
    fi

    build_app
    start_app
    health_check

    header "Deployment completed"
    compose ps
    log "Open the application at http://SERVER_IP:$APP_PORT"
}

command_name="${1:-deploy}"

case "$command_name" in
    deploy)
        deploy
        ;;
    pull)
        pull_source
        ;;
    build)
        check_prerequisites
        build_app
        ;;
    up)
        check_prerequisites
        start_app
        health_check
        ;;
    down)
        check_prerequisites
        header "Stopping application"
        compose down
        log "Application stopped."
        ;;
    restart)
        check_prerequisites
        header "Restarting application"
        compose restart
        health_check
        ;;
    logs)
        check_prerequisites
        compose logs --tail 200 -f warehouse
        ;;
    status|ps)
        check_prerequisites
        compose ps
        docker port "$CONTAINER_NAME" 2>/dev/null || true
        ;;
    health)
        command -v curl >/dev/null 2>&1 || error "curl is not installed."
        health_check
        ;;
    help|-h|--help)
        usage
        ;;
    *)
        usage
        error "Unknown command: $command_name"
        ;;
esac
