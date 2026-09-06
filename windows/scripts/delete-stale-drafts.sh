#!/usr/bin/env bash
# Delete every DRAFT release carrying this tag, before anything is published under it.
#
#   delete-stale-drafts.sh <tag>
#
# A stale DRAFT with this tag (a leftover of an earlier failed run) would make GitHub refuse to
# publish anything else under the tag, so drafts are removed first; they are invisible to users
# and carry nothing a fresh run does not rebuild. The listing is paginated because the draft can
# be arbitrarily far down the release list.
#
# Needs GH_TOKEN and GITHUB_REPOSITORY in the environment (both callers export GH_TOKEN; Actions
# sets GITHUB_REPOSITORY) and gh on PATH.
set -euo pipefail

TAG=${1:?usage: delete-stale-drafts.sh <tag>}
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY must be set}"

gh api --paginate "repos/$GITHUB_REPOSITORY/releases?per_page=100" \
  --jq ".[] | select(.draft and .tag_name == \"$TAG\") | .id" | while read -r id; do
  echo "Deleting stale draft release $id for $TAG"
  gh api -X DELETE "repos/$GITHUB_REPOSITORY/releases/$id"
done
