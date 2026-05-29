import { createCipheriv, createDecipheriv, createHash, createHmac, randomBytes, timingSafeEqual } from "node:crypto";

const NONCE_LENGTH = 12;
const TAG_LENGTH = 16;

export class CultNetClientSecurityOptions {
  readonly #encryptionKey: Buffer;
  readonly connectionKey: string;

  constructor(connectionKey: string) {
    if (!connectionKey || connectionKey.trim().length === 0) {
      throw new Error("Connection key must be provided.");
    }

    this.connectionKey = connectionKey;
    this.#encryptionKey = sha256(Buffer.from(connectionKey, "utf8"));
  }

  static development(): CultNetClientSecurityOptions {
    return new CultNetClientSecurityOptions("gamecult-dev-connection-key");
  }

  getEncryptionKey(): Buffer {
    return Buffer.from(this.#encryptionKey);
  }
}

export class CultNetServerSecurityOptions extends CultNetClientSecurityOptions {
  static readonly CONNECTION_KEY_ENVIRONMENT_VARIABLE = "GAMECULT_CONNECTION_KEY";
  static readonly SESSION_SIGNING_SECRET_ENVIRONMENT_VARIABLE = "GAMECULT_SESSION_SIGNING_SECRET";

  readonly #sessionSigningKey: Buffer;
  readonly sessionSigningSecret: string;
  readonly isDevelopment: boolean;

  constructor(connectionKey: string, sessionSigningSecret: string, isDevelopment = false) {
    super(connectionKey);

    if (!sessionSigningSecret || sessionSigningSecret.trim().length === 0) {
      throw new Error("Session signing secret must be provided.");
    }

    this.sessionSigningSecret = sessionSigningSecret;
    this.#sessionSigningKey = sha256(Buffer.from(sessionSigningSecret, "utf8"));
    this.isDevelopment = isDevelopment;
  }

  static fromEnvironment(
    environment: NodeJS.ProcessEnv = process.env,
    allowDevelopmentDefaults = false,
  ): CultNetServerSecurityOptions {
    const connectionKey = environment[CultNetServerSecurityOptions.CONNECTION_KEY_ENVIRONMENT_VARIABLE];
    const sessionSigningSecret = environment[CultNetServerSecurityOptions.SESSION_SIGNING_SECRET_ENVIRONMENT_VARIABLE];

    const missingConnectionKey = !connectionKey || connectionKey.trim().length === 0;
    const missingSessionSigningSecret = !sessionSigningSecret || sessionSigningSecret.trim().length === 0;

    if (missingConnectionKey && missingSessionSigningSecret) {
      if (allowDevelopmentDefaults) {
        return CultNetServerSecurityOptions.development();
      }

      throw new Error(
        "Server security configuration is not configured. Set GAMECULT_CONNECTION_KEY and GAMECULT_SESSION_SIGNING_SECRET, or explicitly use CultNetServerSecurityOptions.development() for local development.",
      );
    }

    if (missingConnectionKey || missingSessionSigningSecret) {
      const missing = [
        missingConnectionKey ? CultNetServerSecurityOptions.CONNECTION_KEY_ENVIRONMENT_VARIABLE : undefined,
        missingSessionSigningSecret ? CultNetServerSecurityOptions.SESSION_SIGNING_SECRET_ENVIRONMENT_VARIABLE : undefined,
      ].filter((value): value is string => Boolean(value));
      throw new Error(`Server security configuration is partially configured. Missing: ${missing.join(", ")}.`);
    }

    return new CultNetServerSecurityOptions(connectionKey!, sessionSigningSecret!);
  }

  static development(): CultNetServerSecurityOptions {
    return new CultNetServerSecurityOptions(
      "gamecult-dev-connection-key",
      "gamecult-dev-session-signing-secret",
      true,
    );
  }

  getSessionSigningKey(): Buffer {
    return Buffer.from(this.#sessionSigningKey);
  }

  toClientOptions(): CultNetClientSecurityOptions {
    return new CultNetClientSecurityOptions(this.connectionKey);
  }
}

export interface ValidatedCultNetSessionToken {
  userId: string;
  expiresAtUtc: Date;
}

export const CultNetSecret = {
  newNonce(): Uint8Array {
    return randomBytes(NONCE_LENGTH);
  },

  encryptString(
    input: string | null | undefined,
    nonce: Uint8Array,
    options: CultNetClientSecurityOptions | CultNetServerSecurityOptions,
  ): Uint8Array | null {
    if (!input) {
      return null;
    }

    return this.encryptBytes(Buffer.from(input, "utf8"), nonce, options);
  },

  decryptString(
    encrypted: Uint8Array | null | undefined,
    nonce: Uint8Array | null | undefined,
    options: CultNetClientSecurityOptions | CultNetServerSecurityOptions,
  ): string | null {
    if (!encrypted || !nonce) {
      return null;
    }

    return Buffer.from(this.decryptBytes(encrypted, nonce, options)).toString("utf8");
  },

  encryptBytes(
    input: Uint8Array,
    nonce: Uint8Array,
    options: CultNetClientSecurityOptions | CultNetServerSecurityOptions,
  ): Uint8Array {
    const nonceBuffer = validateNonce(nonce);
    const cipher = createCipheriv("aes-256-gcm", options.getEncryptionKey(), nonceBuffer, {
      authTagLength: TAG_LENGTH,
    });
    const ciphertext = Buffer.concat([cipher.update(input), cipher.final()]);
    const tag = cipher.getAuthTag();
    return Buffer.concat([tag, ciphertext]);
  },

  decryptBytes(
    encrypted: Uint8Array,
    nonce: Uint8Array,
    options: CultNetClientSecurityOptions | CultNetServerSecurityOptions,
  ): Uint8Array {
    const nonceBuffer = validateNonce(nonce);
    const encryptedBuffer = Buffer.from(encrypted);

    if (encryptedBuffer.length < TAG_LENGTH) {
      throw new Error("Invalid encrypted data.");
    }

    const tag = encryptedBuffer.subarray(0, TAG_LENGTH);
    const ciphertext = encryptedBuffer.subarray(TAG_LENGTH);
    const decipher = createDecipheriv("aes-256-gcm", options.getEncryptionKey(), nonceBuffer, {
      authTagLength: TAG_LENGTH,
    });
    decipher.setAuthTag(tag);
    return Buffer.concat([decipher.update(ciphertext), decipher.final()]);
  },

  createSessionToken(
    userId: string,
    expiresAtUtc: Date,
    options: CultNetServerSecurityOptions,
  ): string {
    const payload = `${userId}|${Math.floor(expiresAtUtc.getTime() / 1000)}`;
    const payloadBytes = Buffer.from(payload, "utf8");
    const signatureBytes = hmacSha256(options.getSessionSigningKey(), payloadBytes);
    return `${toBase64Url(payloadBytes)}.${toBase64Url(signatureBytes)}`;
  },

  tryValidateSessionToken(
    token: string | null | undefined,
    options: CultNetServerSecurityOptions,
  ): ValidatedCultNetSessionToken | null {
    if (!token || token.trim().length === 0) {
      return null;
    }

    const parts = token.split(".");
    if (parts.length !== 2) {
      return null;
    }

    try {
      const payloadBytes = fromBase64Url(parts[0]);
      const signatureBytes = fromBase64Url(parts[1]);
      const expectedSignature = hmacSha256(options.getSessionSigningKey(), payloadBytes);

      if (signatureBytes.length !== expectedSignature.length || !timingSafeEqual(signatureBytes, expectedSignature)) {
        return null;
      }

      const payload = payloadBytes.toString("utf8");
      const payloadParts = payload.split("|");
      if (payloadParts.length !== 2) {
        return null;
      }

      const [userId, expiresAtUnix] = payloadParts;
      const expiresAtSeconds = Number.parseInt(expiresAtUnix, 10);
      if (!Number.isFinite(expiresAtSeconds)) {
        return null;
      }

      const expiresAtUtc = new Date(expiresAtSeconds * 1000);
      if (expiresAtUtc.getTime() <= Date.now()) {
        return null;
      }

      return {
        userId,
        expiresAtUtc,
      };
    } catch {
      return null;
    }
  },

  toBase64Url(input: Uint8Array): string {
    return toBase64Url(input);
  },

  fromBase64Url(input: string): Uint8Array {
    return fromBase64Url(input);
  },
} as const;

function validateNonce(nonce: Uint8Array): Buffer {
  const value = Buffer.from(nonce);
  if (value.length !== NONCE_LENGTH) {
    throw new Error("Invalid nonce.");
  }

  return value;
}

function sha256(input: Uint8Array): Buffer {
  return createHash("sha256").update(input).digest();
}

function hmacSha256(key: Uint8Array, input: Uint8Array): Buffer {
  return createHmac("sha256", key).update(input).digest();
}

function toBase64Url(input: Uint8Array): string {
  return Buffer.from(input)
    .toString("base64")
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/u, "");
}

function fromBase64Url(input: string): Buffer {
  const normalized = input
    .replace(/-/g, "+")
    .replace(/_/g, "/");
  const remainder = normalized.length % 4;
  const padded = remainder === 0
    ? normalized
    : normalized + "=".repeat(4 - remainder);
  return Buffer.from(padded, "base64");
}
