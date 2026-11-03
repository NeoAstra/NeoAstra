// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.

// The StoreKit 2 side of the purchases plugin of NeoAstra. StoreKit 2 is a Swift API, so this small framework gives it a C
// interface: one JSON request per call, answered with one JSON response through a callback, and the transactions that the
// App Store completes outside a request through a listener. The managed side of the plugin reads the same JSON as the one of
// the Java service on Android.

import Foundation
import StoreKit

public typealias NeoAstraPurchasesCallback = @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<CChar>) -> Void

private struct Context: @unchecked Sendable {
    let callback: NeoAstraPurchasesCallback
    let value: UnsafeMutableRawPointer?

    func answer(_ object: [String: Any]) {
        let data = (try? JSONSerialization.data(withJSONObject: object)) ?? Data("{\"status\":\"Failed\"}".utf8)
        let text = String(decoding: data, as: UTF8.self)
        text.withCString { callback(value, $0) }
    }
}

/// Answers one JSON request on a task of its own; the callback receives the JSON response once.
@_cdecl("neoastra_purchases_call")
public func neoastraPurchasesCall(_ request: UnsafePointer<CChar>, _ callback: NeoAstraPurchasesCallback, _ context: UnsafeMutableRawPointer?) {
    let reply = Context(callback: callback, value: context)
    let input = (try? JSONSerialization.jsonObject(with: Data(String(cString: request).utf8))) as? [String: Any] ?? [:]
    Task.detached {
        reply.answer(await answer(input))
    }
}

/// Reports every transaction that the App Store completes, renews, or revokes outside a request, until the process ends.
@_cdecl("neoastra_purchases_listen")
public func neoastraPurchasesListen(_ callback: NeoAstraPurchasesCallback, _ context: UnsafeMutableRawPointer?) {
    let reply = Context(callback: callback, value: context)
    Task.detached {
        for await update in Transaction.updates {
            reply.answer(["status": "Success", "purchase": purchase(update)])
        }
    }
}

private func answer(_ input: [String: Any]) async -> [String: Any] {
    do {
        switch input["operation"] as? String {
        case "products": return try await products(input)
        case "purchase": return try await buy(input)
        case "list": return ["status": "Success", "purchases": await entitlements()]
        case "restore":
            try await AppStore.sync()
            return ["status": "Success", "purchases": await entitlements()]
        case "finish": return await finish(input)
        default: return ["status": "Unsupported"]
        }
    } catch let error as StoreKitError {
        switch error {
        case .userCancelled: return ["status": "Canceled"]
        case .notAvailableInStorefront: return ["status": "NotFound", "code": "unknown-product"]
        case .notEntitled: return ["status": "Denied", "code": "not-allowed"]
        case .networkError: return ["status": "Failed", "code": "network"]
        default: return ["status": "Failed", "code": "store"]
        }
    } catch let error as Product.PurchaseError {
        switch error {
        case .productUnavailable: return ["status": "NotFound", "code": "unknown-product"]
        case .purchaseNotAllowed: return ["status": "Denied", "code": "not-allowed"]
        default: return ["status": "Failed", "code": "purchase"]
        }
    } catch {
        return ["status": "Failed"]
    }
}

private func products(_ input: [String: Any]) async throws -> [String: Any] {
    let requests = input["products"] as? [[String: Any]] ?? []
    let types = Dictionary(requests.compactMap { item in (item["id"] as? String).map { ($0, item["type"] as? String ?? "InApp") } }, uniquingKeysWith: { first, _ in first })
    let found = try await Product.products(for: Array(types.keys))
    var output: [[String: Any]] = []
    for product in found {
        // The managed side asked for a kind; a product of another kind is not the one it asked for.
        let type = product.type == .autoRenewable ? "Subscription" : "InApp"
        guard types[product.id] == type else { continue }
        var value: [String: Any] = [
            "id": product.id, "type": type, "title": product.displayName, "description": product.description,
            "displayPrice": product.displayPrice, "price": NSDecimalNumber(decimal: product.price).stringValue,
            "currency": product.priceFormatStyle.currencyCode,
        ]
        if let subscription = product.subscription {
            value["period"] = period(subscription.subscriptionPeriod)
            if let offer = subscription.introductoryOffer, await subscription.isEligibleForIntroOffer {
                value["offers"] = [["id": "introductory", "displayPrice": offer.displayPrice, "period": period(offer.period), "introductory": true]]
            }
        }
        output.append(value)
    }
    return ["status": "Success", "products": output]
}

private func buy(_ input: [String: Any]) async throws -> [String: Any] {
    guard let id = input["id"] as? String, let product = try await Product.products(for: [id]).first else { return ["status": "NotFound", "code": "unknown-product"] }
    guard AppStore.canMakePayments else { return ["status": "Denied", "code": "not-allowed"] }
    var options: Set<Product.PurchaseOption> = []
    if let account = (input["accountToken"] as? String).flatMap(UUID.init(uuidString:)) { options.insert(.appAccountToken(account)) }
    switch try await product.purchase(options: options) {
    case .success(let verification):
        return ["status": "Success", "purchase": purchase(verification)]
    case .pending:
        return ["status": "Success", "purchase": ["productId": id, "store": "AppStore", "state": "Pending", "proof": "", "purchaseDate": Int64(Date().timeIntervalSince1970 * 1000)]]
    case .userCancelled:
        return ["status": "Canceled"]
    @unknown default:
        return ["status": "Failed"]
    }
}

// A current entitlement that is not among the unfinished transactions is one that the application finished.
private func entitlements() async -> [[String: Any]] {
    var unfinished = Set<UInt64>()
    for await transaction in Transaction.unfinished { unfinished.insert(transaction.unsafePayloadValue.id) }
    var output: [[String: Any]] = []
    for await entitlement in Transaction.currentEntitlements {
        var value = purchase(entitlement)
        if case .verified(let transaction) = entitlement { value["finished"] = !unfinished.contains(transaction.id) }
        output.append(value)
    }
    return output
}

private func finish(_ input: [String: Any]) async -> [String: Any] {
    guard let id = (input["transactionId"] as? String).flatMap(UInt64.init) else { return ["status": "NotFound"] }
    for await unfinished in Transaction.unfinished {
        if case .verified(let transaction) = unfinished, transaction.id == id {
            await transaction.finish()
            return ["status": "Success"]
        }
    }
    return ["status": "NotFound"]
}

// A transaction with the JWS representation that the server verifies; an unverified one keeps its JWS too, so that the server
// sees why it failed, and is never reported as finished.
private func purchase(_ verification: VerificationResult<Transaction>) -> [String: Any] {
    let transaction = verification.unsafePayloadValue
    var value: [String: Any] = [
        "productId": transaction.productID, "store": "AppStore",
        "state": transaction.revocationDate == nil ? "Purchased" : "Revoked",
        "proof": verification.jwsRepresentation,
        "transactionId": String(transaction.id), "originalTransactionId": String(transaction.originalID),
        "purchaseDate": Int64(transaction.purchaseDate.timeIntervalSince1970 * 1000),
        "quantity": transaction.purchasedQuantity,
    ]
    if let expiration = transaction.expirationDate { value["expirationDate"] = Int64(expiration.timeIntervalSince1970 * 1000) }
    if let account = transaction.appAccountToken { value["accountToken"] = account.uuidString }
    return value
}

private func period(_ period: Product.SubscriptionPeriod) -> String {
    switch period.unit {
    case .day: return "P\(period.value)D"
    case .week: return "P\(period.value)W"
    case .month: return "P\(period.value)M"
    case .year: return "P\(period.value)Y"
    @unknown default: return "P\(period.value)D"
    }
}
