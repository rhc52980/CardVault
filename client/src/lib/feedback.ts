/**
 * Where the Feedback link goes.
 *
 * A new GitHub issue, pre-filled with a short skeleton, the CardVault version
 * and the browser. Nothing from the collection goes into it: the reporter sees
 * the whole form before posting, and a vault's contents have no business in a
 * public issue.
 */

const NEW_ISSUE = 'https://github.com/rhc52980/CardVault/issues/new'

/**
 * The link's full address. `version` comes from the server and is empty until
 * it has answered, so the report still says so rather than showing a blank.
 */
export function feedbackHref(version: string): string {
  const body = [
    '**What happened?**', '', '',
    '**What did you expect instead?**', '', '',
    '**Steps to reproduce**', '', '',
    '---',
    version ? `CardVault v${version}` : 'CardVault (version not yet known)',
    typeof navigator === 'undefined' ? '' : navigator.userAgent,
  ].join('\n')
  return `${NEW_ISSUE}?body=${encodeURIComponent(body)}`
}
