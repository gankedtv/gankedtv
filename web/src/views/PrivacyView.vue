<script setup lang="ts">
import LegalPage from '@/components/LegalPage.vue'
</script>

<template>
  <LegalPage title="Privacy Policy" updated="20 July 2026">
    <p>
      This policy explains what personal data GankedTV processes when you use ganked.tv and its
      related services (the "<strong>Service</strong>"), why, and what rights you have. The data
      controller is [operator legal name] ("<strong>GankedTV</strong>", "we", "us"), reachable at
      [contact e-mail]. This policy is part of our
      <RouterLink to="/terms">Terms of Service</RouterLink>.
    </p>

    <h2>1. Data we process</h2>
    <ul>
      <li>
        <strong>Account data</strong>: username, e-mail address, and (for password accounts) a
        salted password hash. We never store your plaintext password.
      </li>
      <li>
        <strong>Sign-in provider data</strong>: if you sign in with Discord or Google, we receive
        your provider account ID, username, verified e-mail address, and avatar.
      </li>
      <li>
        <strong>Content you post</strong>: clips, thumbnails, comments, likes, follows, profile
        details (bio, avatar, banner, social links), and reports you submit.
      </li>
      <li>
        <strong>Usage and technical data</strong>: IP address, request logs, approximate online
        presence (whether you were recently active), view counts, and technical identifiers used for
        rate limiting and abuse prevention.
      </li>
      <li>
        <strong>Error data</strong>: when something breaks, our self-hosted error monitoring
        receives the technical error report; we configure it not to include personal data beyond
        what is necessary to diagnose the failure.
      </li>
      <li>
        <strong>Analytics</strong>: in production we may use Google Analytics to measure page views.
        Sensitive URL parameters (such as sign-in tokens) are stripped before anything is sent.
      </li>
    </ul>

    <h2>2. Why we process it (legal bases)</h2>
    <ul>
      <li>
        <strong>Running the Service</strong>: accounts, hosting and playing clips, feeds, comments,
        notifications (performance of our contract with you, Art. 6(1)(b) GDPR).
      </li>
      <li>
        <strong>Safety and abuse prevention</strong>: moderation, content reports, rate limiting,
        ban enforcement, security logging (legitimate interest, Art. 6(1)(f)).
      </li>
      <li>
        <strong>Service quality</strong>: error monitoring and aggregate usage statistics
        (legitimate interest, Art. 6(1)(f)).
      </li>
      <li>
        <strong>Legal obligations</strong>: responding to lawful requests from competent authorities
        (Art. 6(1)(c)).
      </li>
    </ul>

    <h2>3. Cookies and local storage</h2>
    <p>
      We use browser storage for what the Service needs to function: your session tokens (to keep
      you signed in) and preferences such as your theme choice. Analytics, where enabled, sets its
      own cookies; everything else is strictly functional.
    </p>

    <h2>4. Who receives your data</h2>
    <p>
      We do not sell personal data. Data is shared only with the infrastructure needed to run the
      Service: our hosting and object-storage providers (which store uploaded media and the
      database), our self-hosted error-monitoring instance, and, if you use them, the sign-in
      providers (Discord, Google) and analytics provider named above. Content you post publicly
      (clips, comments, your profile) is, by design, visible to anyone.
    </p>

    <h2>5. Retention</h2>
    <ul>
      <li>Account data: kept while your account exists; deleted when your account is deleted.</li>
      <li>
        Content: kept until you delete it or it is removed by moderation; transient streaming caches
        and backups cycle out automatically afterwards.
      </li>
      <li>Presence data: expires within minutes of your last activity.</li>
      <li>
        Logs, rate-limit counters, and error reports: kept for short technical windows, then
        discarded.
      </li>
    </ul>

    <h2>6. Your rights</h2>
    <p>
      Under the GDPR you can ask us for access to, correction of, deletion of, or a portable copy of
      your personal data, and you can object to or ask us to restrict processing based on legitimate
      interest. Contact us at [contact e-mail]; we respond within the statutory deadlines. You also
      have the right to lodge a complaint with the Dutch supervisory authority, the Autoriteit
      Persoonsgegevens (autoriteitpersoonsgegevens.nl), or the authority in your own EU member
      state.
    </p>

    <h2>7. Security</h2>
    <p>
      We protect your data with measures appropriate to the risk: passwords are hashed with a modern
      memory-hard algorithm, transport is encrypted, access to production systems is restricted, and
      sign-in tokens are short-lived and revocable.
    </p>

    <h2>8. Children</h2>
    <p>
      The Service is not directed at children under 16, and we do not knowingly process their data.
      If you believe a child under 16 has an account, contact us and we will remove it.
    </p>

    <h2>9. Changes</h2>
    <p>
      We may update this policy from time to time. For material changes we will give reasonable
      advance notice on the site or by e-mail. The "last updated" date above always reflects the
      current version.
    </p>

    <h2>10. Contact</h2>
    <p>Privacy questions and rights requests: [contact e-mail].</p>
  </LegalPage>
</template>
