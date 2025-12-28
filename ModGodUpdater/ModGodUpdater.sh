#!/bin/bash
# ModGod Updater for Linux
# This script syncs mods from a ModGod server to a local SPT installation.
# 
# Features:
# - Downloads and installs mods from your ModGod server
# - Verifies file integrity using SHA256 hashes
# - Supports headless mode for dedicated raid-hosting instances
#
# Headless Mode:
# - Set "headless": true in ModGodData/ModGodClient.json
# - Skips mod downloading, only syncs files explicitly configured for headless
# - Configure headless sync paths in the ModGod UI -> "Headless Syncing" tab

# Don't exit on error - we handle errors ourselves
set +e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Configuration
INTERNAL_DATA_FOLDER="ModGodData"
CONFIG_FILE="ModGodClient.json"
MODS_DOWNLOADED_FILE="modsDownloaded.json"
TEMP_DIR="/tmp/modgod"
LOG_FILE="ModGodUpdater.log"

# Detect SPT root
detect_spt_root() {
    local current_dir=$(pwd)
    
    if [ -d "$current_dir/BepInEx" ] || [ -d "$current_dir/SPT" ]; then
        echo "$current_dir"
    else
        echo ""
    fi
}

log() {
    local msg="[$(date '+%H:%M:%S')] $1"
    echo "$msg" >> "$LOG_FILE" 2>/dev/null || true
    # Also print to stderr for debugging
    if [ "${DEBUG:-false}" = "true" ]; then
        echo "$msg" >&2
    fi
}

log_error() {
    local msg="[$(date '+%H:%M:%S')] ERROR: $1"
    echo "$msg" >> "$LOG_FILE" 2>/dev/null || true
    echo -e "${RED}ERROR:${NC} $1" >&2
}

print_header() {
    echo -e "${CYAN}"
    echo "╔═══════════════════════════════════════╗"
    echo "║         ModGod Updater (Linux)         ║"
    echo "║      SPT Mod Synchronization Tool      ║"
    echo "╚═══════════════════════════════════════╝"
    echo -e "${NC}"
}

print_success() {
    echo -e "${GREEN}✓${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}⚠${NC} $1"
}

print_error() {
    echo -e "${RED}✗${NC} $1"
}

print_info() {
    echo -e "${CYAN}•${NC} $1"
}

# Check dependencies
check_dependencies() {
    local missing=()
    
    if ! command -v curl &> /dev/null; then
        missing+=("curl")
    fi
    
    if ! command -v jq &> /dev/null; then
        missing+=("jq")
    fi
    
    if ! command -v sha256sum &> /dev/null; then
        missing+=("sha256sum (coreutils)")
    fi
    
    if ! command -v unzip &> /dev/null; then
        missing+=("unzip")
    fi
    
    if [ ${#missing[@]} -gt 0 ]; then
        print_error "Missing required dependencies: ${missing[*]}"
        echo "Install them with: sudo apt install ${missing[*]}"
        exit 1
    fi
}

# Load or create config
load_config() {
    local config_path="$DATA_PATH/$CONFIG_FILE"
    HEADLESS_MODE=false
    
    if [ -f "$config_path" ]; then
        SERVER_URL=$(jq -r '.serverUrl // empty' "$config_path" 2>/dev/null)
        HEADLESS_MODE=$(jq -r '.headless // false' "$config_path" 2>/dev/null)
        log "Loaded config: serverUrl=$SERVER_URL, headless=$HEADLESS_MODE"
    fi
    
    if [ -z "$SERVER_URL" ]; then
        echo ""
        print_warning "First time setup - please enter the server URL"
        read -p "Enter server URL (e.g., https://192.168.1.100:6969): " SERVER_URL
        
        # Remove trailing slash
        SERVER_URL="${SERVER_URL%/}"
        
        # Save config preserving headless setting
        jq -n --arg url "$SERVER_URL" --argjson headless "$HEADLESS_MODE" '{serverUrl: $url, headless: $headless}' > "$config_path"
        print_success "Config saved to $config_path"
        print_info "  Set \"headless\": true if this is a headless server"
    else
        print_success "Server: $SERVER_URL"
    fi
    
    # Show mode banner and confirmation
    if [ "$HEADLESS_MODE" = "true" ]; then
        echo ""
        echo -e "${CYAN}╔═══════════════════════════════════════════════════════════════╗${NC}"
        echo -e "${CYAN}║${NC}              ${CYAN}🖥️  HEADLESS MODE${NC}                               ${CYAN}║${NC}"
        echo -e "${CYAN}║${NC}                                                               ${CYAN}║${NC}"
        echo -e "${CYAN}║${NC}  This client is configured as a headless raid-hosting        ${CYAN}║${NC}"
        echo -e "${CYAN}║${NC}  instance. Only files explicitly configured for headless     ${CYAN}║${NC}"
        echo -e "${CYAN}║${NC}  syncing will be downloaded. Mod downloads will be skipped.  ${CYAN}║${NC}"
        echo -e "${CYAN}╚═══════════════════════════════════════════════════════════════╝${NC}"
        echo ""
        log "Running in HEADLESS mode"
        
        # Confirmation prompt
        read -p "Continue in headless mode? [Y/n] " -n 1 -r
        echo ""
        if [[ $REPLY =~ ^[Nn]$ ]]; then
            echo -e "${YELLOW}Operation cancelled. Edit ModGodClient.json to change mode.${NC}"
            log "User cancelled headless mode operation"
            exit 0
        fi
        echo ""
    else
        echo ""
        echo -e "${GREEN}╔═══════════════════════════════════════════════════════════════╗${NC}"
        echo -e "${GREEN}║${NC}              ${GREEN}🎮  STANDARD MODE${NC}                               ${GREEN}║${NC}"
        echo -e "${GREEN}║${NC}                                                               ${GREEN}║${NC}"
        echo -e "${GREEN}║${NC}  This client will download and sync all configured mods      ${GREEN}║${NC}"
        echo -e "${GREEN}║${NC}  and files from the ModGod server.                           ${GREEN}║${NC}"
        echo -e "${GREEN}╚═══════════════════════════════════════════════════════════════╝${NC}"
        echo ""
        log "Running in STANDARD mode"
        
        # Confirmation prompt
        read -p "Continue with mod sync? [Y/n] " -n 1 -r
        echo ""
        if [[ $REPLY =~ ^[Nn]$ ]]; then
            echo -e "${YELLOW}Operation cancelled.${NC}"
            log "User cancelled standard mode operation"
            exit 0
        fi
        echo ""
    fi
}

# Fetch server config
fetch_server_config() {
    local url="$SERVER_URL/modgod/api/config"
    
    log "Fetching server config from $url"
    
    # Use -k to accept self-signed certificates, capture both response and http code
    local temp_file="$TEMP_DIR/config_response.json"
    local http_code
    http_code=$(curl -ks --max-time 30 -w "%{http_code}" -o "$temp_file" "$url" 2>>"$LOG_FILE")
    local curl_exit=$?
    
    if [ $curl_exit -ne 0 ]; then
        log_error "curl failed with exit code $curl_exit"
        print_error "Failed to connect to server (curl error $curl_exit)"
        return 1
    fi
    
    log "HTTP response code: $http_code"
    
    if [ "$http_code" != "200" ]; then
        log_error "Server returned HTTP $http_code"
        if [ -f "$temp_file" ]; then
            log "Response body:"
            cat "$temp_file" >> "$LOG_FILE" 2>/dev/null
        fi
        print_error "Server returned HTTP $http_code"
        rm -f "$temp_file"
        return 1
    fi
    
    if [ ! -f "$temp_file" ] || [ ! -s "$temp_file" ]; then
        log_error "Empty response from server"
        print_error "Empty response from server"
        rm -f "$temp_file"
        return 1
    fi
    
    cat "$temp_file"
    rm -f "$temp_file"
}

# Fetch manifest
fetch_manifest() {
    local url="$SERVER_URL/modgod/api/manifest"
    
    # Use headless endpoint for headless clients
    if [ "$HEADLESS_MODE" = "true" ]; then
        url="$SERVER_URL/modgod/api/manifest/headless"
    fi
    
    log "Fetching manifest from $url (headless=$HEADLESS_MODE)"
    
    local temp_file="$TEMP_DIR/manifest_response.json"
    local http_code
    http_code=$(curl -ks --max-time 60 -w "%{http_code}" -o "$temp_file" "$url" 2>>"$LOG_FILE")
    local curl_exit=$?
    
    if [ $curl_exit -ne 0 ]; then
        log_error "curl failed fetching manifest with exit code $curl_exit"
        rm -f "$temp_file"
        return 1
    fi
    
    log "Manifest HTTP response code: $http_code"
    
    if [ "$http_code" != "200" ]; then
        log_error "Server returned HTTP $http_code for manifest"
        rm -f "$temp_file"
        return 1
    fi
    
    if [ ! -f "$temp_file" ] || [ ! -s "$temp_file" ]; then
        log_error "Empty manifest response"
        rm -f "$temp_file"
        return 1
    fi
    
    cat "$temp_file"
    rm -f "$temp_file"
}

# Download a single file
# Args: relative_path [headless_mode]
download_file() {
    local relative_path="$1"
    local is_headless="${2:-false}"
    local encoded_path=$(echo "$relative_path" | jq -Rr @uri 2>/dev/null)
    local url="$SERVER_URL/modgod/api/file/$encoded_path"
    
    # Add headless query parameter if in headless mode
    if [ "$is_headless" = "true" ]; then
        url="${url}?headless=true"
    fi
    
    local target_path="$SPT_ROOT/$relative_path"
    
    log "Downloading file: $url -> $target_path"
    
    # Create directory if needed
    local target_dir=$(dirname "$target_path")
    mkdir -p "$target_dir" 2>/dev/null
    
    # Download file
    local http_code
    http_code=$(curl -ks --max-time 300 -w "%{http_code}" -o "$target_path" "$url" 2>>"$LOG_FILE")
    local curl_exit=$?
    
    if [ $curl_exit -ne 0 ]; then
        log_error "curl failed downloading file with exit code $curl_exit"
        return 1
    fi
    
    if [ "$http_code" != "200" ]; then
        log_error "Server returned HTTP $http_code for file: $relative_path"
        rm -f "$target_path" 2>/dev/null
        return 1
    fi
    
    if [ ! -f "$target_path" ]; then
        log_error "File not created after download: $target_path"
        return 1
    fi
    
    log "Successfully downloaded: $relative_path"
    return 0
}

# Calculate file hash
get_file_hash() {
    sha256sum "$1" 2>/dev/null | cut -d' ' -f1 | tr '[:upper:]' '[:lower:]'
}

# Process mod download
process_mod() {
    local mod_json="$1"
    local mod_name=$(echo "$mod_json" | jq -r '.modName' 2>/dev/null)
    local download_url=$(echo "$mod_json" | jq -r '.downloadUrl' 2>/dev/null)
    local last_updated=$(echo "$mod_json" | jq -r '.lastUpdated' 2>/dev/null)
    local is_protected=$(echo "$mod_json" | jq -r '.isProtected' 2>/dev/null)
    
    log "Processing mod: $mod_name"
    
    # Skip protected mods
    if [ "$is_protected" = "true" ]; then
        print_success "$mod_name (installed)"
        return 0
    fi
    
    # Skip if no download URL
    if [ -z "$download_url" ] || [ "$download_url" = "null" ]; then
        print_success "$mod_name (installed)"
        return 0
    fi
    
    # Check if already downloaded with same version
    local downloaded_path="$DATA_PATH/$MODS_DOWNLOADED_FILE"
    if [ -f "$downloaded_path" ]; then
        local existing=$(jq -r --arg url "$download_url" '.[] | select(.downloadUrl == $url) | .lastUpdated' "$downloaded_path" 2>/dev/null)
        if [ "$existing" = "$last_updated" ]; then
            print_success "$mod_name (up to date)"
            return 0
        fi
    fi
    
    # Download mod
    print_info "Downloading $mod_name..."
    log "Downloading $mod_name from $download_url"
    
    # Generate unique IDs without uuidgen (fallback for systems without it)
    local unique_id=$(date +%s%N)_$$
    local temp_archive="$TEMP_DIR/${unique_id}.archive"
    local temp_extract="$TEMP_DIR/${unique_id}_extract"
    
    mkdir -p "$temp_extract"
    if [ $? -ne 0 ]; then
        log_error "Failed to create temp directory: $temp_extract"
        return 1
    fi
    
    log "Downloading to: $temp_archive"
    local curl_output
    curl_output=$(curl -ksL --max-time 600 -w "%{http_code}" -o "$temp_archive" "$download_url" 2>&1)
    local curl_exit=$?
    
    if [ $curl_exit -ne 0 ]; then
        log_error "curl failed with exit code $curl_exit: $curl_output"
        print_error "$mod_name - download failed (curl error $curl_exit)"
        rm -rf "$temp_archive" "$temp_extract"
        return 1
    fi
    
    # Check if file was downloaded
    if [ ! -f "$temp_archive" ]; then
        log_error "Download file not created: $temp_archive"
        print_error "$mod_name - download failed (no file)"
        rm -rf "$temp_extract"
        return 1
    fi
    
    local file_size=$(stat -c%s "$temp_archive" 2>/dev/null || stat -f%z "$temp_archive" 2>/dev/null || echo "0")
    log "Downloaded file size: $file_size bytes"
    
    if [ "$file_size" -lt 100 ]; then
        log_error "Downloaded file too small ($file_size bytes), likely an error response"
        cat "$temp_archive" >> "$LOG_FILE" 2>/dev/null || true
        print_error "$mod_name - download failed (invalid response)"
        rm -rf "$temp_archive" "$temp_extract"
        return 1
    fi
    
    # Extract archive
    local archive_type=$(file -b --mime-type "$temp_archive" 2>/dev/null)
    log "Archive type: $archive_type"
    
    local extract_result=0
    case "$archive_type" in
        application/zip)
            log "Extracting zip archive..."
            unzip -q -o "$temp_archive" -d "$temp_extract" 2>>"$LOG_FILE"
            extract_result=$?
            ;;
        application/x-7z-compressed)
            log "Extracting 7z archive..."
            7z x -y -o"$temp_extract" "$temp_archive" >> "$LOG_FILE" 2>&1
            extract_result=$?
            ;;
        application/x-rar*)
            log "Extracting rar archive..."
            unrar x -y "$temp_archive" "$temp_extract" >> "$LOG_FILE" 2>&1
            extract_result=$?
            ;;
        application/gzip|application/x-gzip)
            log "Extracting gzip archive..."
            tar -xzf "$temp_archive" -C "$temp_extract" 2>>"$LOG_FILE"
            extract_result=$?
            ;;
        application/x-tar)
            log "Extracting tar archive..."
            tar -xf "$temp_archive" -C "$temp_extract" 2>>"$LOG_FILE"
            extract_result=$?
            ;;
        *)
            log_error "Unknown archive type: $archive_type"
            print_error "$mod_name - unknown archive type: $archive_type"
            rm -rf "$temp_archive" "$temp_extract"
            return 1
            ;;
    esac
    
    if [ $extract_result -ne 0 ]; then
        log_error "Extraction failed with exit code $extract_result"
        print_error "$mod_name - extraction failed"
        rm -rf "$temp_archive" "$temp_extract"
        return 1
    fi
    
    rm -f "$temp_archive"
    
    # List extracted contents for debugging
    log "Extracted contents:"
    ls -la "$temp_extract" >> "$LOG_FILE" 2>&1
    
    # Get install paths from mod_json
    local install_paths=$(echo "$mod_json" | jq -c '.installPaths // []' 2>/dev/null)
    log "Install paths: $install_paths"
    
    # Copy files according to install paths
    local path_count=$(echo "$install_paths" | jq 'length' 2>/dev/null)
    log "Number of install paths: $path_count"
    
    for i in $(seq 0 $((path_count - 1))); do
        local path_pair=$(echo "$install_paths" | jq -c ".[$i]" 2>/dev/null)
        local source=$(echo "$path_pair" | jq -r '.[0]' 2>/dev/null)
        # Handle both old format (<SPT_ROOT>/path) and new format (path) for backwards compatibility
        local target_raw=$(echo "$path_pair" | jq -r '.[1]' 2>/dev/null)
        local target_rel=$(echo "$target_raw" | sed 's|<SPT_ROOT>||g' | sed 's|^/||')
        local target="$SPT_ROOT/$target_rel"
        
        local source_path="$temp_extract/$source"
        log "Copying: $source_path -> $target"
        
        if [ -d "$source_path" ]; then
            mkdir -p "$target"
            cp -rf "$source_path"/* "$target/" 2>>"$LOG_FILE"
            log "Copied directory: $source_path to $target"
        elif [ -f "$source_path" ]; then
            mkdir -p "$(dirname "$target")"
            cp -f "$source_path" "$target" 2>>"$LOG_FILE"
            log "Copied file: $source_path to $target"
        else
            log "Warning: Source path not found: $source_path"
        fi
    done
    
    rm -rf "$temp_extract"
    
    # Update mods downloaded list
    if [ ! -f "$downloaded_path" ]; then
        echo "[]" > "$downloaded_path"
    fi
    
    local new_entry=$(jq -n --arg name "$mod_name" --arg url "$download_url" --arg updated "$last_updated" \
        '{modName: $name, downloadUrl: $url, lastUpdated: $updated, optIn: true}' 2>/dev/null)
    
    # Remove existing entry for this URL and add new one
    jq --arg url "$download_url" --argjson entry "$new_entry" \
        '[.[] | select(.downloadUrl != $url)] + [$entry]' "$downloaded_path" > "$downloaded_path.tmp" 2>>"$LOG_FILE" \
        && mv "$downloaded_path.tmp" "$downloaded_path"
    
    print_success "$mod_name (installed)"
    log "Successfully installed: $mod_name"
    return 0
}

# Sync files based on manifest
sync_files() {
    log "Starting file sync (headless=$HEADLESS_MODE)..."
    
    # Track success/failure counts
    local total_success=0
    local total_fail=0
    
    if [ "$HEADLESS_MODE" = "true" ]; then
        echo -e "${CYAN}Headless File Sync${NC}"
        echo -e "${YELLOW}Syncing only headless-specific files...${NC}"
    else
        echo "File Verification"
    fi
    echo ""
    
    local manifest=$(fetch_manifest)
    
    if [ -z "$manifest" ]; then
        print_warning "Could not fetch file manifest. Skipping file sync."
        log "Warning: Could not fetch manifest"
        return 0
    fi
    
    local file_count=$(echo "$manifest" | jq '.files | length' 2>/dev/null)
    
    if [ "$HEADLESS_MODE" = "true" ]; then
        print_success "Headless Manifest: $file_count files configured for sync"
        if [ "$file_count" -eq 0 ]; then
            print_warning "No files configured for headless syncing. Configure in ModGod UI."
            return 0
        fi
    else
        print_success "Manifest: $file_count files from server"
    fi
    log "Manifest contains $file_count files"
    
    # Get sync roots from manifest (for extra file detection)
    local sync_roots_file="$TEMP_DIR/sync_roots.txt"
    echo "$manifest" | jq -r '.syncRoots[]? // empty' > "$sync_roots_file" 2>/dev/null
    
    # Get exclusions from manifest
    local exclusions_file="$TEMP_DIR/exclusions.txt"
    echo "$manifest" | jq -r '.syncExclusions[]? // empty' > "$exclusions_file" 2>/dev/null
    
    # Create issues file
    local issues_file="$TEMP_DIR/issues.txt"
    > "$issues_file"
    
    # Save file entries to temp file for processing
    local entries_file="$TEMP_DIR/entries.json"
    echo "$manifest" | jq -c '.files | to_entries[]' > "$entries_file" 2>/dev/null
    
    # Build a set of manifest files for extra file detection
    local manifest_files="$TEMP_DIR/manifest_files.txt"
    echo "$manifest" | jq -r '.files | keys[]' > "$manifest_files" 2>/dev/null
    
    # Check each file
    log "Verifying files..."
    while IFS= read -r entry; do
        if [ -z "$entry" ]; then
            continue
        fi
        
        local relative_path=$(echo "$entry" | jq -r '.key' 2>/dev/null)
        local expected_hash=$(echo "$entry" | jq -r '.value.hash' 2>/dev/null)
        local full_path="$SPT_ROOT/$relative_path"
        
        if [ ! -f "$full_path" ]; then
            echo "missing:$relative_path" >> "$issues_file"
        else
            local local_hash=$(get_file_hash "$full_path")
            if [ "$local_hash" != "$expected_hash" ]; then
                echo "modified:$relative_path" >> "$issues_file"
            fi
        fi
    done < "$entries_file"
    
    rm -f "$entries_file"
    
    # Scan for extra files (skip for headless mode)
    if [ "$HEADLESS_MODE" != "true" ]; then
        log "Scanning for extra files..."
        
        # Use syncRoots from manifest if available, otherwise default
        local has_sync_roots=$(wc -l < "$sync_roots_file" 2>/dev/null | tr -d ' ')
        if [ "${has_sync_roots:-0}" -eq 0 ]; then
            echo "BepInEx/plugins" > "$sync_roots_file"
            echo "SPT/user/mods" >> "$sync_roots_file"
        fi
        
        log "Sync roots: $(cat "$sync_roots_file" | tr '\n' ', ')"
        
        while IFS= read -r sync_root; do
            if [ -z "$sync_root" ]; then
                continue
            fi
            
            local full_sync_dir="$SPT_ROOT/$sync_root"
            if [ ! -d "$full_sync_dir" ]; then
                continue
            fi
            
            log "Scanning sync root: $full_sync_dir"
            
            # Find all files in this sync root
            find "$full_sync_dir" -type f 2>/dev/null | while IFS= read -r file; do
                local relative_path=$(realpath --relative-to="$SPT_ROOT" "$file" 2>/dev/null || echo "$file" | sed "s|^$SPT_ROOT/||")
                relative_path=$(echo "$relative_path" | sed 's|\\|/|g')
                
                # Check if in manifest
                if ! grep -qxF "$relative_path" "$manifest_files" 2>/dev/null; then
                    # Check if excluded
                    local is_excluded=false
                    while IFS= read -r exclusion; do
                        if [ -z "$exclusion" ]; then
                            continue
                        fi
                        # Simple prefix match for non-glob patterns
                        if [[ "$relative_path" == "$exclusion"* ]]; then
                            is_excluded=true
                            break
                        fi
                    done < "$exclusions_file"
                    
                    if [ "$is_excluded" = "false" ]; then
                        echo "extra:$relative_path" >> "$issues_file"
                    fi
                fi
            done
        done < "$sync_roots_file"
    fi
    
    rm -f "$sync_roots_file" "$exclusions_file" "$manifest_files"
    
    # Count issues (grep -c returns 0 count but exits 1 when no matches, so we capture separately)
    local missing=0
    local modified=0
    local extra=0
    missing=$(grep -c "^missing:" "$issues_file" 2>/dev/null) || true
    modified=$(grep -c "^modified:" "$issues_file" 2>/dev/null) || true
    extra=$(grep -c "^extra:" "$issues_file" 2>/dev/null) || true
    # Ensure we have valid integers
    missing=${missing:-0}
    modified=${modified:-0}
    extra=${extra:-0}
    
    log "Issues found: $missing missing, $modified modified, $extra extra"
    
    if [ "$missing" -eq 0 ] && [ "$modified" -eq 0 ] && [ "$extra" -eq 0 ]; then
        print_success "All files verified - no issues found!"
        rm -f "$issues_file"
        return 0
    fi
    
    echo ""
    print_warning "Found issues:"
    [ "$missing" -gt 0 ] && echo -e "  ${RED}• $missing missing file(s)${NC}"
    [ "$modified" -gt 0 ] && echo -e "  ${YELLOW}• $modified modified file(s)${NC}"
    [ "$extra" -gt 0 ] && echo -e "  ${CYAN}• $extra extra file(s)${NC}"
    
    # Download missing files
    if [ "$missing" -gt 0 ]; then
        echo ""
        print_info "Downloading $missing missing file(s)..."
        
        # Use a temp file to track counts (subshell issue workaround)
        local success_count_file="$TEMP_DIR/success_count"
        local fail_count_file="$TEMP_DIR/fail_count"
        echo "0" > "$success_count_file"
        echo "0" > "$fail_count_file"
        
        grep "^missing:" "$issues_file" | cut -d: -f2- | while IFS= read -r file_path; do
            log "Downloading missing file: $file_path"
            if download_file "$file_path" "$HEADLESS_MODE"; then
                print_success "  $file_path"
                echo $(( $(cat "$success_count_file") + 1 )) > "$success_count_file"
            else
                print_error "  $file_path - failed"
                log_error "Failed to download: $file_path"
                echo $(( $(cat "$fail_count_file") + 1 )) > "$fail_count_file"
            fi
        done
        
        total_success=$(( total_success + $(cat "$success_count_file" 2>/dev/null || echo 0) ))
        total_fail=$(( total_fail + $(cat "$fail_count_file" 2>/dev/null || echo 0) ))
        rm -f "$success_count_file" "$fail_count_file"
    fi
    
    # Handle modified files
    if [ "$modified" -gt 0 ]; then
        echo ""
        print_info "Updating $modified modified file(s)..."
        
        local success_count_file="$TEMP_DIR/success_count"
        local fail_count_file="$TEMP_DIR/fail_count"
        echo "0" > "$success_count_file"
        echo "0" > "$fail_count_file"
        
        grep "^modified:" "$issues_file" | cut -d: -f2- | while IFS= read -r file_path; do
            log "Updating modified file: $file_path"
            if download_file "$file_path" "$HEADLESS_MODE"; then
                print_success "  $file_path"
                echo $(( $(cat "$success_count_file") + 1 )) > "$success_count_file"
            else
                print_error "  $file_path - failed"
                log_error "Failed to update: $file_path"
                echo $(( $(cat "$fail_count_file") + 1 )) > "$fail_count_file"
            fi
        done
        
        total_success=$(( total_success + $(cat "$success_count_file" 2>/dev/null || echo 0) ))
        total_fail=$(( total_fail + $(cat "$fail_count_file" 2>/dev/null || echo 0) ))
        rm -f "$success_count_file" "$fail_count_file"
    fi
    
    # Handle extra files
    if [ "$extra" -gt 0 ]; then
        echo ""
        echo -e "${CYAN}Extra Files${NC}"
        echo -e "${YELLOW}These files exist locally but are not in the server's mod list.${NC}"
        echo ""
        
        echo "How do you want to handle $extra extra file(s)?"
        echo "  1) Delete all"
        echo "  2) Keep all"
        read -p "Choice [1-2] (default: 2): " choice
        
        if [ "$choice" = "1" ]; then
            grep "^extra:" "$issues_file" | cut -d: -f2- | while IFS= read -r file_path; do
                local full_path="$SPT_ROOT/$file_path"
                if rm -f "$full_path" 2>/dev/null; then
                    print_error "  Deleted: $file_path"
                    log "Deleted extra file: $file_path"
                else
                    print_warning "  Failed to delete: $file_path"
                    log_error "Failed to delete: $file_path"
                fi
            done
        else
            echo -e "${YELLOW}Keeping all extra files.${NC}"
            log "User chose to keep extra files"
        fi
    fi
    
    rm -f "$issues_file"
    
    # Show summary if there were any download attempts
    if [ "$total_success" -gt 0 ] || [ "$total_fail" -gt 0 ]; then
        echo ""
        if [ "$total_fail" -gt 0 ]; then
            echo -e "${YELLOW}Summary:${NC} ${GREEN}$total_success succeeded${NC}, ${RED}$total_fail failed${NC}"
            log "Sync summary: $total_success succeeded, $total_fail failed"
        else
            echo -e "${GREEN}Summary:${NC} All $total_success file(s) downloaded successfully"
            log "Sync summary: All $total_success files downloaded successfully"
        fi
    fi
}

# Error handler
handle_error() {
    local line_no=$1
    local error_code=$2
    log_error "Script failed at line $line_no with exit code $error_code"
    print_error "Script failed at line $line_no. Check $LOG_FILE for details."
}

# Main function
main() {
    # Set up error trap
    trap 'handle_error ${LINENO} $?' ERR
    
    print_header
    
    # Check dependencies
    check_dependencies
    
    # Detect SPT root
    SPT_ROOT=$(detect_spt_root)
    
    if [ -z "$SPT_ROOT" ]; then
        print_error "ModGodUpdater.sh must be run from your SPT root directory."
        echo "Expected structure:"
        echo "  SPT/"
        echo "  ├── BepInEx/"
        echo "  ├── SPT/"
        echo "  ├── ModGodData/"
        echo "  └── ModGodUpdater.sh"
        exit 1
    fi
    
    DATA_PATH="$SPT_ROOT/$INTERNAL_DATA_FOLDER"
    mkdir -p "$DATA_PATH"
    mkdir -p "$TEMP_DIR"
    
    # Initialize logging
    LOG_FILE="$SPT_ROOT/$LOG_FILE"
    > "$LOG_FILE"  # Clear log file
    log "ModGod Updater (Linux) started"
    log "SPT Root: $SPT_ROOT"
    log "Bash version: $BASH_VERSION"
    log "Date: $(date)"
    
    print_success "SPT Root: $SPT_ROOT"
    echo ""
    
    # Load config
    load_config
    
    # Fetch server config
    echo ""
    echo "Fetching server mod list..."
    log "Fetching server config..."
    local server_config=$(fetch_server_config)
    
    if [ -z "$server_config" ]; then
        log_error "Failed to fetch server config - empty response"
        print_error "Failed to fetch server config"
        exit 1
    fi
    
    # Validate JSON
    if ! echo "$server_config" | jq empty 2>/dev/null; then
        log_error "Invalid JSON response from server:"
        echo "$server_config" >> "$LOG_FILE"
        print_error "Invalid response from server (not JSON)"
        exit 1
    fi
    
    local mod_count=$(echo "$server_config" | jq '.modList | length' 2>/dev/null)
    if [ -z "$mod_count" ]; then
        log_error "Could not parse modList from server config"
        log "Server response: $server_config"
        print_error "Invalid server response (no modList)"
        exit 1
    fi
    
    log "Server returned $mod_count mod(s)"
    print_success "Found $mod_count mod(s) on server"
    
    # Process mods (skip for headless clients)
    if [ "$HEADLESS_MODE" = "true" ]; then
        echo ""
        echo -e "${CYAN}ℹ️${NC} Skipping mod downloads (headless mode)"
        log "Skipping mod downloads (headless mode)"
    else
        echo ""
        echo "Processing mods..."
        echo ""
        
        # Save mod list to temp file to avoid subshell issues with piping
        local mods_file="$TEMP_DIR/mods_to_process.json"
        echo "$server_config" | jq -c '.modList[] | select(.optional != true)' > "$mods_file" 2>/dev/null
        
        log "Processing required mods..."
        while IFS= read -r mod; do
            if [ -n "$mod" ]; then
                process_mod "$mod"
                local result=$?
                if [ $result -ne 0 ]; then
                    log_error "Failed to process mod"
                fi
            fi
        done < "$mods_file"
        
        # Handle optional mods
        echo "$server_config" | jq -c '.modList[] | select(.optional == true)' > "$mods_file" 2>/dev/null
        local optional_count=$(wc -l < "$mods_file" 2>/dev/null | tr -d ' ')
        
        if [ "$optional_count" -gt 0 ]; then
            echo ""
            echo "Optional mods:"
            log "Processing optional mods..."
            while IFS= read -r mod; do
                if [ -n "$mod" ]; then
                    process_mod "$mod"
                fi
            done < "$mods_file"
        fi
        
        rm -f "$mods_file"
    fi
    
    # Sync files
    echo ""
    if [ "$HEADLESS_MODE" = "true" ]; then
        echo "Starting headless file sync..."
    else
        echo "Verifying files..."
    fi
    sync_files
    
    # Cleanup
    cleanup
    
    echo ""
    print_success "Sync complete!"
    log "Sync complete"
    echo ""
    print_info "Log file: $LOG_FILE"
}

# Cleanup function
cleanup() {
    log "Cleaning up temp files..."
    rm -rf "$TEMP_DIR" 2>/dev/null || true
}

# Run main with error handling
{
    main "$@"
    exit_code=$?
} || {
    exit_code=$?
    log_error "Script terminated with exit code $exit_code"
    cleanup
}

exit ${exit_code:-0}
