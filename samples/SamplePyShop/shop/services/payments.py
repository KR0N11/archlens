from abc import ABC, abstractmethod

import requests


class PaymentGateway(ABC):
    @abstractmethod
    def charge(self, amount: float) -> str: ...


class StripeGateway(PaymentGateway):
    def charge(self, amount: float) -> str:
        return requests.post("https://api.stripe.com/v1/charges", data={"amount": amount}).text


class FakeGateway(PaymentGateway):
    def charge(self, amount: float) -> str:
        return "fake"
